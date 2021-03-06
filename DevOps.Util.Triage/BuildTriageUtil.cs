
#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using DevOps.Util;
using DevOps.Util.DotNet;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Octokit;
using HelixTimelineResult = DevOps.Util.DotNet.TimelineResult<string>;

namespace DevOps.Util.Triage
{
    internal sealed class BuildTriageUtil
    {
        internal DevOpsServer Server { get; }

        internal DotNetQueryUtil QueryUtil { get; }

        internal TriageContextUtil TriageContextUtil { get; }

        private ILogger Logger { get; }

        internal Build Build { get; }

        internal BuildInfo BuildInfo { get; }

        internal ModelBuild ModelBuild { get; }

        internal Timeline? Timeline { get; set; }

        internal bool HasSetTimeline { get; set; }

        internal List<HelixWorkItem>? HelixWorkItems { get; set; }

        internal List<(HelixWorkItem WorkItem, HelixLogInfo LogInfo)>? HelixLogInfos { get; set; }

        internal Dictionary<string, HelixTimelineResult>? HelixJobToRecordMap { get; set; }

        internal TriageContext Context => TriageContextUtil.Context;

        internal BuildTriageUtil(
            Build build,
            BuildInfo buildInfo,
            ModelBuild modelBuild,
            DevOpsServer server,
            TriageContextUtil triageContextUtil,
            ILogger logger)
        {
            Build = build;
            BuildInfo = buildInfo;
            ModelBuild = modelBuild;
            Server = server;
            TriageContextUtil = triageContextUtil;
            QueryUtil = new DotNetQueryUtil(server);
            Logger = logger;
        }

        internal async Task TriageAsync()
        {
            Logger.LogInformation($"Triaging {DevOpsUtil.GetBuildUri(Build)}");

            var query = 
                from issue in Context.ModelTriageIssues
                from complete in Context.ModelTriageIssueResultCompletes
                    .Where(complete =>
                        complete.ModelTriageIssueId == issue.Id &&
                        complete.ModelBuildId == ModelBuild.Id)
                    .DefaultIfEmpty()
                select new { issue, complete };

            foreach (var data in query.ToList())
            {
                if (data.complete is object)
                {
                    continue;
                }

                var issue = data.issue;
                switch (issue.SearchKind)
                {
                    case SearchKind.SearchTimeline:
                        await DoSearchTimelineAsync(issue);
                        break;
                    case SearchKind.SearchHelixRunClient:
                        await DoSearchHelixAsync(issue, HelixLogKind.RunClientUri);
                        break;
                    case SearchKind.SearchHelixConsole:
                        await DoSearchHelixAsync(issue, HelixLogKind.Console);
                        break;
                    case SearchKind.SearchHelixTestResults:
                        await DoSearchHelixAsync(issue, HelixLogKind.TestResults);
                        break;
                    default:
                        Logger.LogWarning($"Unknown search kind {issue.SearchKind} in {issue.Id}");
                        break;
                }
            }
        }

        private async Task DoSearchTimelineAsync(ModelTriageIssue modelTriageIssue)
        {
            var timeline = await EnsureTimelineAsync().ConfigureAwait(false);
            if (timeline is object)
            {
                DoSearchTimeline(modelTriageIssue, timeline);
            }
        }

        private void DoSearchTimeline(ModelTriageIssue modelTriageIssue, Timeline timeline)
        {
            var searchText = modelTriageIssue.SearchText;
            Logger.LogInformation($@"Text: ""{searchText}""");
            if (TriageContextUtil.IsProcessed(modelTriageIssue, ModelBuild))
            {
                Logger.LogInformation($@"Skipping");
                return;
            }

            var count = 0;
            foreach (var result in QueryUtil.SearchTimeline(Build, timeline, text: searchText))
            {
                count++;

                var modelTriageIssueResult = new ModelTriageIssueResult()
                {
                    TimelineRecordName = result.RecordName,
                    JobName = result.JobName,
                    Line = result.Value.Line,
                    ModelBuild = ModelBuild,
                    ModelTriageIssue = modelTriageIssue,
                    BuildNumber = result.Value.Build.GetBuildKey().Number,
                };
                Context.ModelTriageIssueResults.Add(modelTriageIssueResult);
            }

            var complete = new ModelTriageIssueResultComplete()
            {
                ModelTriageIssue = modelTriageIssue,
                ModelBuild = ModelBuild,
            };
            Context.ModelTriageIssueResultCompletes.Add(complete);

            try
            {
                Logger.LogInformation($@"Saving {count} jobs");
                Context.SaveChanges();
            }
            catch (Exception ex)
            {
                Logger.LogError($"Cannot save timeline complete: {ex.Message}");
            }
        }

        private async Task DoSearchHelixAsync(ModelTriageIssue modelTriageIssue, HelixLogKind kind)
        {
            await EnsureHelixLogInfosAsync().ConfigureAwait(false);
            Debug.Assert(HelixLogInfos is object);
            await DoSearchHelixAsync(modelTriageIssue, kind, HelixLogInfos);
        }

        private async Task DoSearchHelixAsync(
            ModelTriageIssue modelTriageIssue,
            HelixLogKind kind,
            List<(HelixWorkItem WorkItem, HelixLogInfo LogInfo)> helixLogInfos)
        {
            Logger.LogInformation($@"Helix Search {kind}: ""{modelTriageIssue.SearchText}""");

            // Guarding against a catostrophic PR failure here. When more than this many work items
            // fail abandon auto-triage
            //
            // The number chosen here is 100% arbitrary. Can adjust as more data comes in.
            if (helixLogInfos.Count > 10)
            {
                Logger.LogWarning($@"Too many results, not searching");
                return;
            }

            var count = 0;
            var jobMap = await EnsureHelixJobToRecordMap().ConfigureAwait(false);
            foreach (var tuple in helixLogInfos)
            {
                var workItem = tuple.WorkItem;
                var helixLogInfo = tuple.LogInfo;
                var uri = helixLogInfo.GetUri(kind);
                if (uri is null)
                {
                    continue;
                }

                // TODO: should cache the log download so it can be used in multiple searches
                Logger.LogInformation($"Downloading helix log {kind} {uri}");
                var isMatch = await QueryUtil.SearchFileForAnyMatchAsync(
                    uri,
                    DotNetQueryUtil.CreateSearchRegex(modelTriageIssue.SearchText),
                    ex => Logger.LogWarning($"Error searching log: {ex.Message}"));
                if (isMatch)
                {
                    string? recordName = null;
                    string? jobName = null;
                    if (jobMap.TryGetValue(workItem.JobId, out var result))
                    {
                        recordName = result.RecordName;
                        jobName = result.JobName;
                    }

                    // TODO: missing all the timeline record data here
                    var modelTriageIssueResult = new ModelTriageIssueResult()
                    {
                        HelixJobId = workItem.JobId,
                        HelixWorkItem = workItem.WorkItemName,
                        ModelBuild = ModelBuild,
                        ModelTriageIssue = modelTriageIssue,
                        BuildNumber = BuildInfo.Number,
                        TimelineRecordName = recordName,
                        JobName = jobName,
                    };
                    Context.ModelTriageIssueResults.Add(modelTriageIssueResult);
                    count++;
                }
            }

            var complete = new ModelTriageIssueResultComplete()
            {
                ModelTriageIssue = modelTriageIssue,
                ModelBuild = ModelBuild,
            };
            Context.ModelTriageIssueResultCompletes.Add(complete);

            // TODO: should batch all the saves and not do it issue by issue
            try
            {
                Logger.LogInformation($@"Saving {count} helix info");
                Context.SaveChanges();
            }
            catch (Exception ex)
            {
                Logger.LogError($"Cannot save helix complete: {ex.Message}");
            }
        }

        private async Task EnsureHelixWorkItemsAsync()
        {
            if (HelixWorkItems is object)
            {
                return;
            }

            try
            {
                HelixWorkItems = await QueryUtil.ListHelixWorkItemsAsync(
                    Build,
                    DotNetUtil.FailedTestOutcomes);
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"Error getting helix work items: {ex.Message}");
            }

            HelixWorkItems ??= new List<HelixWorkItem>();
        }

        private async Task EnsureHelixLogInfosAsync()
        {
            if (HelixLogInfos is object)
            {
                return;
            }

            await EnsureHelixWorkItemsAsync().ConfigureAwait(false);
            Debug.Assert(HelixWorkItems is object);

            var list = new List<(HelixWorkItem, HelixLogInfo)>();
            foreach (var helixWorkItem in HelixWorkItems)
            {
                try
                {
                    var helixLogInfo = await HelixUtil.GetHelixLogInfoAsync(Server, helixWorkItem).ConfigureAwait(false);
                    list.Add((helixWorkItem, helixLogInfo));
                }
                catch (Exception ex)
                {
                    Logger.LogWarning($"Error getting log info for {helixWorkItem.HelixInfo}: {ex.Message}");
                }
            }

            HelixLogInfos = list;
        }

        private async Task<Timeline?> EnsureTimelineAsync()
        {
            if (HasSetTimeline)
            {
                return Timeline;
            }

            try
            {
                Timeline = await Server.GetTimelineAsync(Build);
                if (Timeline is null)
                {
                    Logger.LogWarning("No timeline");
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"Error getting timeline: {ex.Message}");
            }

            HasSetTimeline = true;
            return Timeline;
        }

        private async Task<Dictionary<string, HelixTimelineResult>> EnsureHelixJobToRecordMap()
        {
            if (HelixJobToRecordMap is object)
            {
                return HelixJobToRecordMap;
            }

            HelixJobToRecordMap = new Dictionary<string, HelixTimelineResult>(StringComparer.OrdinalIgnoreCase);
            var timeline = await EnsureTimelineAsync().ConfigureAwait(false);
            if (timeline is null)
            {
                return HelixJobToRecordMap;
            }

            try
            {
                foreach (var result in await QueryUtil.ListHelixJobs(timeline).ConfigureAwait(false))
                {
                    HelixJobToRecordMap[result.Value] = result;
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"Error getting helix job to record map: {ex.Message}");
            }

            return HelixJobToRecordMap;
        }
    }
}
