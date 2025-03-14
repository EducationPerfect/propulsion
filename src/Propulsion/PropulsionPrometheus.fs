// This file implements a Serilog Sink `LogSink` that publishes metric values to Prometheus.
namespace Propulsion.Prometheus

[<AutoOpen>]
module private Impl =

    let baseName stat = "propulsion_scheduler_" + stat
    let baseDesc desc = "Propulsion Scheduler " + desc
    let groupLabels = [| "group"; "state" |]
    let groupWithKindLabels = [| "kind"; "group"; "state" |]
    let activityLabels = [| "group"; "activity" |]
    let latencyLabels = [| "group"; "kind" |]

    let [<Literal>] secondsStat = "_seconds"
    let [<Literal>] latencyDesc = " latency"

    let append = Array.append

module private Gauge =

    let private make (config : Prometheus.GaugeConfiguration) name desc =
        let gauge = Prometheus.Metrics.CreateGauge(name, desc, config)
        fun tagValues group state value ->
            let labelValues = append tagValues [| group; state |]
            gauge.WithLabels(labelValues).Set(value)

    let create (tagNames, tagValues) stat desc =
        let config = Prometheus.GaugeConfiguration(LabelNames = append tagNames groupLabels)
        make config (baseName stat) (baseDesc desc) tagValues
    let createWithKind (tagNames, tagValues) kind stat desc =
        let config = Prometheus.GaugeConfiguration(LabelNames = append tagNames groupWithKindLabels)
        make config (baseName stat) (baseDesc desc) (Array.append tagValues [| kind |])

module private Counter =

    let private make (config : Prometheus.CounterConfiguration) name desc =
        let counter = Prometheus.Metrics.CreateCounter(name, desc, config)
        fun tagValues group activity value ->
            let labelValues = append tagValues [| group; activity |]
            counter.WithLabels(labelValues).Inc(value)

    let create (tagNames, tagValues) stat desc =
        let config = Prometheus.CounterConfiguration(LabelNames = append tagNames activityLabels)
        make config (baseName stat) (baseDesc desc) tagValues

module private Summary =

    let private create (config : Prometheus.SummaryConfiguration) name desc  =
        let summary = Prometheus.Metrics.CreateSummary(name, desc, config)
        fun tagValues (group, kind) value ->
            let labelValues = append tagValues [| group; kind |]
            summary.WithLabels(labelValues).Observe(value)

    let private objectives =
           [|
               0.50, 0.05 // Between 45th and 55th percentile
               0.95, 0.01 // Between 94th and 96th percentile
               0.99, 0.01 // Between 100th and 98th percentile
           |] |> Array.map Prometheus.QuantileEpsilonPair

    let latency (tagNames, tagValues) stat desc =
        let config =
            let labelValues = append tagNames latencyLabels
            Prometheus.SummaryConfiguration(Objectives = objectives, LabelNames = labelValues, MaxAge = System.TimeSpan.FromMinutes 1.)
        create config (baseName stat + secondsStat) (baseDesc desc + latencyDesc) tagValues

module private Histogram =

    let private create (config : Prometheus.HistogramConfiguration) name desc =
        let histogram = Prometheus.Metrics.CreateHistogram(name, desc, config)
        fun tagValues (group, kind) value ->
            let labelValues = append tagValues [| group; kind |]
            histogram.WithLabels(labelValues).Observe(value)

    let private sBuckets =
        Prometheus.Histogram.ExponentialBuckets(0.001, 2., 16) // 1ms .. 64s

    let latency (tagNames, tagValues) stat desc =
        let config = Prometheus.HistogramConfiguration(Buckets = sBuckets, LabelNames = append tagNames latencyLabels)
        create config (baseName stat + secondsStat) (baseDesc desc + latencyDesc) tagValues

open Propulsion.Streams.Log

/// <summary>An ILogEventSink that publishes to Prometheus</summary>
/// <param name="customTags">Custom tags to annotate the metric we're publishing where such tag manipulation cannot better be achieved via the Prometheus scraper config.</param>
/// <param name="defaultGroup">ChangeFeedProcessor <c>processorName</c>. It's recommended to supply this via <c>logger.ForContext("group") where possible</c></param>
type LogSink(customTags: seq<string * string>, ?defaultGroup: string) =

    let tags = Array.ofSeq customTags |> Array.unzip
    // TOCONSIDER In V3, have Ingesters and Projectors be tagged with a consumer group for metrics/logging purposes to sidestep this hackery
    let defaultGroup () =
        match defaultGroup with
        | Some g -> g
        | None -> invalidArg "group" "Propulsion.Streams Metrics events must each bear a ForContext(\"group\") value if you do not supply one for the Sink"

    let observeCats =    Gauge.create      tags "cats"            "Current categories"
    let observeStreams = Gauge.create      tags "streams"         "Current streams"
    let observeEvents =  Gauge.create      tags "events"          "Current events"
    let observeBytes =   Gauge.create      tags "bytes"           "Current bytes"

    let observeBusyCount = Gauge.create    tags "busy_count"      "Current Busy Streams count"
    let observeBusyOldest = Gauge.createWithKind tags "oldest" "busy_seconds" "Busy Streams age, seconds"
    let observeBusyNewest = Gauge.createWithKind tags "newest" "busy_seconds" "Busy Streams age, seconds"

    let observeCpu =     Counter.create    tags "cpu"             "Processing Time Breakdown"

    let observeLatSum =  Summary.latency   tags "handler_summary" "Handler action"
    let observeLatHis =  Histogram.latency tags "handler"         "Handler action"

    let observeState group state (m : BufferMetric) =
        observeCats group state (float m.cats)
        observeStreams group state (float m.streams)
        observeEvents group state (float m.events)
        observeBytes group state (float m.bytes)
    let observeLatency group kind latency =
        observeLatSum (group, kind) latency
        observeLatHis (group, kind) latency
    let observeBusy group kind count oldest newest =
        observeBusyCount group kind (float count)
        observeBusyOldest group kind oldest
        observeBusyNewest group kind newest

    interface Serilog.Core.ILogEventSink with
        member _.Emit logEvent = logEvent |> function
            | MetricEvent (e, maybeContextGroup) ->
                let group = maybeContextGroup |> ValueOption.defaultWith defaultGroup
                match e with
                | Metric.BufferReport m ->
                    observeState group "ingesting" m
                | Metric.SchedulerStateReport (synced, busyStats, readyStats, bufferingStats, malformedStats) ->
                    observeStreams group "synced" (float synced)
                    observeState group "active" busyStats
                    observeState group "ready" readyStats
                    observeState group "buffering" bufferingStats
                    observeState group "malformed" malformedStats
                | Metric.SchedulerCpu (merge, ingest, dispatch, results, stats) ->
                    observeCpu group "merge" merge.TotalSeconds
                    observeCpu group "ingest" ingest.TotalSeconds
                    observeCpu group "dispatch" dispatch.TotalSeconds
                    observeCpu group "results" results.TotalSeconds
                    observeCpu group "stats" stats.TotalSeconds
                | Metric.HandlerResult (kind, latency) ->
                    observeLatency group kind latency
                | Metric.StreamsBusy (kind, count, oldest, newest) ->
                    observeBusy group kind count oldest newest
            | _ -> ()
