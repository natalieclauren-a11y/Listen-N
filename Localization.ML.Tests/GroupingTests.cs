using System;
using System.Collections.Generic;
using System.Linq;
using Localization.ML;
using Xunit;

namespace Localization.ML.Tests;

public class GroupingTests
{
    [Fact]
    public void GetGroupId_IsInvariantToDualOrdering()
    {
        var firstOrder = new LocalizationRow
        {
            Channels = new double[FeatureBuilder.ChannelCount],
            DurationSeconds = 1,
            IsDual = true,
            DualCoordinates = new[] { 1.0, 2.0, 3.0, 4.0, 5.0, 6.0 }
        };

        var swappedOrder = new LocalizationRow
        {
            Channels = new double[FeatureBuilder.ChannelCount],
            DurationSeconds = 1,
            IsDual = true,
            DualCoordinates = new[] { 4.0, 5.0, 6.0, 1.0, 2.0, 3.0 }
        };

        var id1 = Grouping.GetGroupId(firstOrder);
        var id2 = Grouping.GetGroupId(swappedOrder);

        Assert.Equal(id1, id2);
    }

    [Fact]
    public void GroupSplit_ProducesDisjointGroupIds()
    {
        var rows = new List<LocalizationRow>
        {
            new()
            {
                Channels = new double[FeatureBuilder.ChannelCount],
                DurationSeconds = 1,
                IsDual = false,
                SingleCoordinates = new[] { 1.0, 2.0, 3.0 }
            },
            new()
            {
                Channels = new double[FeatureBuilder.ChannelCount],
                DurationSeconds = 1,
                IsDual = false,
                SingleCoordinates = new[] { 1.0, 2.0, 3.0 }
            },
            new()
            {
                Channels = new double[FeatureBuilder.ChannelCount],
                DurationSeconds = 1,
                IsDual = false,
                SingleCoordinates = new[] { 10.0, 20.0, 30.0 }
            }
        };

        var split = Grouping.GroupSplit(rows, 0.5, seed: 1);
        var trainGroups = split.Train.Select(Grouping.GetGroupId).ToHashSet(StringComparer.Ordinal);
        var holdoutGroups = split.Holdout.Select(Grouping.GetGroupId).ToHashSet(StringComparer.Ordinal);

        Assert.Empty(trainGroups.Intersect(holdoutGroups));
    }
}
