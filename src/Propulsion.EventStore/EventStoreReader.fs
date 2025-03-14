﻿module Propulsion.EventStore.Reader

open EventStore.ClientAPI
open Propulsion.Internal // Sem
open Propulsion.Sinks
open Serilog // NB Needs to shadow ILogger
open System
open System.Collections.Generic
open System.Threading

let inline arrayBytes (x : byte[]) = match x with null -> 0 | x -> x.Length
let inline recPayloadBytes (x : RecordedEvent) = arrayBytes x.Data + arrayBytes x.Metadata
let inline payloadBytes (x : ResolvedEvent) = recPayloadBytes x.Event + x.OriginalStreamId.Length * sizeof<char>
let private dash = [|'-'|]

// Bespoke algorithm suited to grouping streams as observed in EventStore, where {category}-{streamId} is expected, but definitely not guaranteed
let private categorizeEventStoreStreamId (eventStoreStreamId : string) =
    eventStoreStreamId.Split(dash, 2, StringSplitOptions.RemoveEmptyEntries).[0]

/// Maintains ingestion stats (thread safe via lock free data structures so it can be used across multiple overlapping readers)
type OverallStats(?statsInterval) =
    let overallStart, interval = Stopwatch.start (), IntervalTimer(defaultArg statsInterval (TimeSpan.FromMinutes 5.))
    let mutable totalEvents, totalBytes = 0L, 0L

    member _.Ingest(batchEvents, batchBytes) =
        Interlocked.Add(&totalEvents, batchEvents) |> ignore
        Interlocked.Add(&totalBytes, batchBytes) |> ignore

    member _.Bytes = totalBytes
    member _.Events = totalEvents

    member _.DumpIfIntervalExpired(?force) =
        if defaultArg force false then interval.Trigger()
        if interval.IfDueRestart() && totalEvents <> 0L then
            let totalMb = Log.miB totalBytes
            Log.Information("Reader Throughput {events} events {gb:n1}GB {mb:n2}MB/s",
                totalEvents, totalMb / 1024., totalMb * 1000. / float overallStart.ElapsedMilliseconds)

/// Maintains stats for traversals of $all; Threadsafe [via naive locks] so can be used by multiple stripes reading concurrently
type SliceStatsBuffer(?interval) =
    let interval = IntervalTimer(defaultArg interval (TimeSpan.FromMinutes 5.))
    let recentCats = Dictionary<string, int * int>()

    member x.Ingest(slice : AllEventsSlice) =
        lock recentCats <| fun () ->
            let mutable batchBytes = 0
            for x in slice.Events do
                let cat = categorizeEventStoreStreamId x.OriginalStreamId
                let eventBytes = payloadBytes x
                match recentCats.TryGetValue cat with
                | true, (currCount, currSize) -> recentCats[cat] <- (currCount + 1, currSize + eventBytes)
                | false, _ -> recentCats[cat] <- (1, eventBytes)
                batchBytes <- batchBytes + eventBytes
            x.DumpIfIntervalExpired()
            slice.Events.Length, int64 batchBytes

    member _.DumpIfIntervalExpired(?force) =
        if defaultArg force false then interval.Trigger()
        if interval.IfDueRestart() then
            lock recentCats <| fun () ->
                let log kind limit xs =
                    let cats =
                        [| for KeyValue (s, (c, b)) in xs |> Seq.sortBy (fun (KeyValue (_, (_, b))) -> -b) ->
                            Log.miB (int64 b) |> round, s, c |]
                    if Array.any cats then
                        let mb, events, top = Array.sumBy (fun (mb, _, _) -> mb) cats, Array.sumBy (fun (_, _, c) -> c) cats, Seq.truncate limit cats
                        Log.Information("Reader {kind} {mb:n0}MB {events:n0} events categories: {@cats} (MB/cat/count)", kind, mb, events, top)
                recentCats |> log "Total" 3
                recentCats |> Seq.where (fun x -> x.Key.StartsWith "$" |> not) |> log "payload" 100
                recentCats |> Seq.where (fun x -> x.Key.StartsWith "$") |> log "meta" 100
                recentCats.Clear()

/// Defines a tranche of a traversal of a stream (or the store as a whole)
type Range(start, sliceEnd : Position option, ?max : Position) =
    member val Current = start with get, set

    member x.TryNext(pos : Position) =
        x.Current <- pos
        x.IsCompleted

    member x.IsCompleted =
        match sliceEnd with
        | Some send when x.Current.CommitPosition >= send.CommitPosition -> false
        | _ -> true

    member x.PositionAsRangePercentage =
        match max with
        | None -> Double.NaN
        | Some max ->
            match x.Current.CommitPosition, max.CommitPosition with
            | p,m when p > m -> Double.NaN
            | p,m -> float p / float m

(* Logic for computation of chunk offsets; ES writes chunks whose index starts at a multiple of 256MB
   to be able to address an arbitrary position as a percentage, we need to consider this aspect as only a valid Position can be supplied to the read call *)

// @scarvel8: event_global_position = 256 x 1024 x 1024 x chunk_number + chunk_header_size (128) + event_position_offset_in_chunk
let chunk (pos : Position) = uint64 pos.CommitPosition >>> 28
let posFromChunk (chunk : int) =
    let chunkBase = int64 chunk * 1024L * 1024L * 256L
    Position(chunkBase, 0L)
let posFromChunkAfter (pos : Position) =
    let nextChunk = 1 + int (chunk pos)
    posFromChunk nextChunk
let posFromPercentage (pct, max : Position) =
    let rawPos = Position(float max.CommitPosition * pct / 100. |> int64, 0L)
    let chunk = int (chunk rawPos) in posFromChunk chunk // &&& 0xFFFFFFFFE0000000L // rawPos / 256L / 1024L / 1024L * 1024L * 1024L * 256L

/// Read the current tail position; used to be able to compute and log progress of ingestion
let fetchMax (conn : IEventStoreConnection) = task {
    let! lastItemBatch = conn.ReadAllEventsBackwardAsync(Position.End, 1, resolveLinkTos=false)
    let max = lastItemBatch.FromPosition
    Log.Information("EventStore Tail Position: @ {pos} ({chunks} chunks, ~{gb:n1}GB)", max.CommitPosition, chunk max, Log.miB max.CommitPosition/1024.)
    return max }

/// `fetchMax` wrapped in a retry loop; Sync process is heavily reliant on establishing the max in order to be able to show progress % so we have a crude retry loop
let establishMax (conn : IEventStoreConnection) = task {
    let mutable max = None
    while Option.isNone max do
        try let! currentMax = fetchMax conn
            max <- Some currentMax
        with e ->
            Log.Warning(e, "Could not establish max position")
            do! Async.Sleep 5000
    return Option.get max }

/// Walks a stream within the specified constraints; used to grab data when writing to a stream for which a prefix is missing
/// Can throw (in which case the caller is in charge of retrying, possibly with a smaller batch size)
let pullStream (conn : IEventStoreConnection, batchSize) (stream, pos, limit : int option) mapEvent (postBatch : string * Event[] -> Async<unit>) =
    let rec fetchFrom pos limit = async {
        let reqLen = match limit with Some limit -> min limit batchSize | None -> batchSize
        let! currentSlice = conn.ReadStreamEventsForwardAsync(stream, pos, reqLen, resolveLinkTos=true) |> Async.ofTask
        let events = currentSlice.Events |> Array.map (fun x -> mapEvent x.Event)
        do! postBatch (stream, events)
        match limit with
        | None when currentSlice.IsEndOfStream -> return ()
        | None -> return! fetchFrom currentSlice.NextEventNumber None
        | Some limit when events.Length >= limit -> return ()
        | Some limit -> return! fetchFrom currentSlice.NextEventNumber (Some (limit - events.Length)) }
    fetchFrom pos limit

/// Walks the $all stream, yielding batches together with the associated Position info for the purposes of checkpointing
/// Can throw (in which case the caller is in charge of retrying, possibly with a smaller batch size)
type [<NoComparison>] PullResult = Exn of exn: exn | Eof | EndOfTranche
let pullAll (slicesStats : SliceStatsBuffer, overallStats : OverallStats) (conn : IEventStoreConnection, batchSize)
        (range:Range, once) (tryMapEvent : ResolvedEvent -> StreamEvent option) (postBatch : Position -> StreamEvent[] -> Async<struct (int * int)>) =
    let sw = Stopwatch.start () // we'll report the warmup/connect time on the first batch
    let streams, cats = HashSet(), HashSet()
    let rec aux () = async {
        let! currentSlice = conn.ReadAllEventsForwardAsync(range.Current, batchSize, resolveLinkTos=false) |> Async.ofTask
        sw.Stop() // Stop the clock after the read call completes; transition to measuring time to traverse / filter / submit
        let postTs = Stopwatch.timestamp ()
        let batchEvents, batchBytes = slicesStats.Ingest currentSlice in overallStats.Ingest(int64 batchEvents, batchBytes)
        let events = currentSlice.Events |> Seq.choose tryMapEvent |> Array.ofSeq
        streams.Clear(); cats.Clear()
        for struct (sn, _) in events do
            if streams.Add sn then
                cats.Add(Propulsion.Streams.StreamName.categorize sn) |> ignore
        let! cur, max = postBatch currentSlice.NextPosition events
        Log.Information("Read {pos,10} {pct:p1} {ft:n3}s {mb:n1}MB {count,4} {categories,4}c {streams,4}s {events,4}e Post {pt:n3}s {cur}/{max}",
                        range.Current.CommitPosition, range.PositionAsRangePercentage, (let e = sw.Elapsed in e.TotalSeconds), Log.miB batchBytes,
                        batchEvents, cats.Count, streams.Count, events.Length, Stopwatch.elapsedSeconds postTs, cur, max)
        if not (range.TryNext currentSlice.NextPosition && not once && not currentSlice.IsEndOfStream) then
            if currentSlice.IsEndOfStream then return Eof
            else return EndOfTranche
        else
            sw.Restart() // restart the clock as we hand off back to the Reader
            return! aux () }
    async {
        try return! aux ()
        with e -> return Exn e }

/// Specification for work to be performed by a reader thread
[<NoComparison>]
type Req =
    | EofDetected
    /// Tail from a given start position, at intervals of the specified timespan (no waiting if catching up)
    | Tail of seriesId : int * startPos : Position * max : Position * interval : TimeSpan * batchSize : int
    // Read a given segment of a stream (used when a stream needs to be rolled forward to lay down an event for which the preceding events are missing)
    //| StreamPrefix of name: string * pos: int64 * len: int * batchSize: int
    // Read the entirety of a stream in blocks of the specified batchSize (TODO wire to commandline request)
    //| Stream of name: string * batchSize: int
    /// Read a specific chunk (min-max range), posting batches tagged with that chunk number
    | Chunk of seriesId : int * range: Range * batchSize : int

/// Data with context resulting from a reader thread
[<NoComparison; NoEquality; RequireQualifiedAccess>]
type Res =
    /// A batch read from a Chunk
    | Batch of seriesId : int * pos : Position * items : StreamEvent seq
    /// Ingestion buffer requires an explicit end of chunk message before next chunk can commence processing
    | EndOfChunk of seriesId : int
    // A Batch read from a Stream or StreamPrefix
    //| StreamSpan of span: State.StreamSpan

/// Holds work queue, together with stats relating to the amount and/or categories of data being traversed
/// Processing is driven by external callers running multiple concurrent invocations of `Process`
type EventStoreReader(connections : _[], defaultBatchSize, minBatchSize, tryMapEvent, post : Res -> Async<struct (int * int)>, tailInterval, dop, ?statsInterval) =
    let work = System.Collections.Concurrent.ConcurrentQueue()
    let sleepIntervalMs = 100
    let overallStats = OverallStats(?statsInterval=statsInterval)
    let slicesStats = SliceStatsBuffer()

    /// Invoked by pump to process a tranche of work; can have parallel invocations
    let exec conn req = async {
        let adjust batchSize = if batchSize > minBatchSize then max minBatchSize (batchSize - 128) else batchSize
        //let postSpan = ReadResult.StreamSpan >> post >> Async.Ignore
        match req with
        | EofDetected as x -> failwithf "Unexpected %A" x

        //| StreamPrefix (name, pos, len, batchSize) ->
        //    use _ = Serilog.Context.LogContext.PushProperty("Tranche", name)
        //    Log.Warning("Reading stream prefix; pos {pos} len {len} batch size {bs}", pos, len, batchSize)
        //    try let! t,() = pullStream (conn, batchSize) (name, pos, Some len) postSpan |> Stopwatch.Time
        //        Log.Information("completed stream prefix in {ms:n3}s", let e = t.Elapsed in e.TotalSeconds)
        //    with e ->
        //        let bs = adjust batchSize
        //        Log.Warning(e, "Could not read stream, retrying with batch size {bs}", bs)
        //        x.AddStreamPrefix(name, pos, len, bs)
        //    return false
        //| Stream (name, batchSize) ->
        //    use _ = Serilog.Context.LogContext.PushProperty("Tranche", name)
        //    Log.Warning("Reading stream; batch size {bs}", batchSize)
        //    try let! t,() = pullStream (conn, batchSize) (name, 0L, None) postSpan |> Stopwatch.Time
        //        Log.Information("completed stream in {ms:n3}s", let e = t.Elapsed in e.TotalSeconds)
        //    with e ->
        //        let bs = adjust batchSize
        //        Log.Warning(e, "Could not read stream, retrying with batch size {bs}", bs)
        //        x.AddStream(name, bs)
        //    return false

        | Chunk (series, range, batchSize) ->
            let postBatch pos items = post (Res.Batch (series, pos, items))
            use _ = Serilog.Context.LogContext.PushProperty("Tranche", series)
            Log.Information("Commencing tranche, batch size {bs}", batchSize)
            let t = Stopwatch.start ()
            match! pullAll (slicesStats, overallStats) (conn, batchSize) (range, false) tryMapEvent postBatch with
            | PullResult.Eof ->
                Log.Warning("completed tranche AND REACHED THE END in {ms:n3}m", t.ElapsedMinutes)
                let! _ = post (Res.EndOfChunk series) in ()
                work.Enqueue EofDetected
            | PullResult.EndOfTranche ->
                Log.Information("completed tranche in {ms:n1}m", t.ElapsedMinutes)
                let! _ = post (Res.EndOfChunk series) in ()
            | PullResult.Exn e ->
                let abs = adjust batchSize
                Log.Warning(e, "Could not read All, retrying with batch size {bs}", abs)
                work.Enqueue <| Req.Chunk (series, range, abs)

        | Tail (series, pos, max, interval, batchSize) ->
            let postBatch pos items = post (Res.Batch (series, pos, items))
            use _ = Serilog.Context.LogContext.PushProperty("Tranche", "Tail")
            let mutable count, batchSize, range = 0, batchSize, Range(pos, None, max)
            let statsInterval = defaultArg statsInterval (TimeSpan.FromMinutes 5.)
            let tailIntervalMs = int64 interval.TotalMilliseconds
            let tailSw = Stopwatch.start ()
            let awaitInterval = async {
                match tailIntervalMs - tailSw.ElapsedMilliseconds |> int with
                | waitTimeMs when waitTimeMs > 0 -> do! Async.Sleep waitTimeMs
                | _ -> ()
                tailSw.Restart() }
            let slicesStats, stats = SliceStatsBuffer(), OverallStats()
            let progressInterval = IntervalTimer statsInterval
            while true do
                if progressInterval.IfDueRestart() then
                    let currentPos = range.Current
                    Log.Information("Tailed {count} times @ {pos} (chunk {chunk})",
                        count, currentPos.CommitPosition, chunk currentPos)
                count <- count + 1
                let! res = pullAll (slicesStats, stats) (conn, batchSize) (range, true) tryMapEvent postBatch
                do! awaitInterval
                match res with
                | PullResult.EndOfTranche | PullResult.Eof _ -> ()
                | PullResult.Exn e ->
                    batchSize <- adjust batchSize
                    Log.Warning(e, "Tail $all failed, adjusting batch size to {bs}", batchSize) }

    member _.Pump(initialSeriesId, initialPos, max) = async {
        let mutable robin = 0
        let selectConn () =
            let connIndex = Interlocked.Increment(&robin) % connections.Length
            connections[connIndex]

        let dop = Sem dop
        let forkRunRelease =
            let r = Random()
            fun req -> async { // this is not called in parallel hence no need to lock `r`
                let capacity = let struct (used, max) = dop.State in max - used
                // Jitter is most relevant when processing commences - any commencement of a chunk can trigger significant page faults on server
                // which we want to attempt to limit the effects of
                let jitterMs = if capacity = 0 then 200 else r.Next(1000, 2000)
                Log.Information("Waiting {jitter}ms to jitter reader stripes, {currentCount} further reader stripes awaiting start", jitterMs, capacity)
                do! Async.Sleep jitterMs
                let! _ = Async.StartChild <| async {
                    try let conn = selectConn ()
                        do! exec conn req
                    finally dop.Release() } in () }

        let mutable seriesId = initialSeriesId
        let mutable remainder =
            if connections.Length > 1 then
                let nextPos = posFromChunkAfter initialPos
                work.Enqueue <| Req.Chunk (seriesId, Range(initialPos, Some nextPos, max), defaultBatchSize)
                Some nextPos
            else
                work.Enqueue <| Req.Tail (seriesId, initialPos, max, tailInterval, defaultBatchSize)
                None

        let! ct = Async.CancellationToken
        let mutable endDetected = false
        while not ct.IsCancellationRequested do
            overallStats.DumpIfIntervalExpired()
            let! _ = dop.Wait ct |> Async.ofUnitTask
            match work.TryDequeue(), remainder with
            | (true, EofDetected), Some nextChunk ->
                if endDetected then
                    dop.Release()
                else
                    Log.Warning("No further ingestion work to commence, transitioning to tailing...")
                    overallStats.DumpIfIntervalExpired(true)
                    endDetected <- true
                    remainder <- None
                    seriesId <- seriesId + 1
                    // TODO shed excess connections as transitioning
                    do! forkRunRelease <| Req.Tail (seriesId, nextChunk, nextChunk, tailInterval, defaultBatchSize)
            // Process requeuing etc
            | (true, task), _ ->
                do! forkRunRelease task
            // Start a chunk if no work and eof detection has yet to call a halt
            | (false, _), Some nextChunk ->
                seriesId <- seriesId + 1
                let nextPos = posFromChunkAfter nextChunk
                remainder <- Some nextPos
                do! forkRunRelease <| Req.Chunk (seriesId, Range(nextChunk, Some nextPos, max), defaultBatchSize)
            // Otherwise sleep for remainder of interval
            | (false, _), None ->
                dop.Release()
                do! Async.Sleep sleepIntervalMs }
