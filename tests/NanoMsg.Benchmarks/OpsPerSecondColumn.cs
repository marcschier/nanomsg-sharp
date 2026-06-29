// Copyright (c) marcschier. Licensed under the MIT License.

using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using Perfolizer.Horology;

namespace NanoMsg.Benchmarks;

/// <summary>A column that prints throughput as operations per second (the inverse of the mean op time).</summary>
public sealed class OpsPerSecondColumn : IColumn
{
    /// <inheritdoc/>
    public string Id => nameof(OpsPerSecondColumn);

    /// <inheritdoc/>
    public string ColumnName => "Ops/sec";

    /// <inheritdoc/>
    public string Legend => "Operations (messages or round trips) completed per second";

    /// <inheritdoc/>
    public bool AlwaysShow => true;

    /// <inheritdoc/>
    public ColumnCategory Category => ColumnCategory.Statistics;

    /// <inheritdoc/>
    public int PriorityInCategory => 1;

    /// <inheritdoc/>
    public bool IsNumeric => true;

    /// <inheritdoc/>
    public UnitType UnitType => UnitType.Dimensionless;

    /// <inheritdoc/>
    public bool IsAvailable(Summary summary) => true;

    /// <inheritdoc/>
    public bool IsDefault(Summary summary, BenchmarkCase benchmarkCase) => false;

    /// <inheritdoc/>
    public string GetValue(Summary summary, BenchmarkCase benchmarkCase)
    {
        BenchmarkReport? report = summary[benchmarkCase];
        double meanNs = report?.ResultStatistics?.Mean ?? 0;
        if (meanNs <= 0)
        {
            return "NA";
        }

        double opsPerSecond = TimeInterval.Second.Nanoseconds / meanNs;
        return opsPerSecond.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <inheritdoc/>
    public string GetValue(Summary summary, BenchmarkCase benchmarkCase, SummaryStyle style) =>
        GetValue(summary, benchmarkCase);
}

/// <summary>The default BenchmarkDotNet configuration plus the <see cref="OpsPerSecondColumn"/>.</summary>
public sealed class BenchConfig : ManualConfig
{
    /// <summary>Initializes a new instance of the <see cref="BenchConfig"/> class.</summary>
    public BenchConfig() => AddColumn(new OpsPerSecondColumn());
}
