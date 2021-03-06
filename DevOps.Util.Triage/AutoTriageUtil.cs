#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using DevOps.Util;
using DevOps.Util.DotNet;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Octokit;

namespace DevOps.Util.Triage
{
    public sealed class AutoTriageUtil
    {
        public DevOpsServer Server { get; }

        public DotNetQueryUtil QueryUtil { get; }

        public TriageContextUtil TriageContextUtil { get; }

        private ILogger Logger { get; }

        public TriageContext Context => TriageContextUtil.Context;

        public AutoTriageUtil(
            DevOpsServer server,
            TriageContext context,
            ILogger logger)
        {
            Server = server;
            QueryUtil = new DotNetQueryUtil(server);
            TriageContextUtil = new TriageContextUtil(context);
            Logger = logger;
        }

        // TODO: don't do this if the issue is closed
        // TODO: limit builds to report on to 100 because after that the tables get too large

        // TODO: eventually this won't be necessary
        public void EnsureTriageIssues()
        {
            TriageContextUtil.EnsureTriageIssue(
                TriageIssueKind.Infra,
                SearchKind.SearchTimeline,
                searchText: "unable to load shared library 'advapi32.dll' or one of its dependencies",
                Create("dotnet", "core-eng", 9635));
            TriageContextUtil.EnsureTriageIssue(
                TriageIssueKind.Infra,
                SearchKind.SearchTimeline,
                searchText: "HTTP request to.*api.nuget.org.*timed out",
                Create("dotnet", "core-eng", 9634, "-p public"),
                Create("dotnet", "runtime", 35074));
            TriageContextUtil.EnsureTriageIssue(
                TriageIssueKind.Infra,
                SearchKind.SearchTimeline,
                searchText: "Failed to install dotnet",
                Create("dotnet", "runtime", 34015));
            TriageContextUtil.EnsureTriageIssue(
                TriageIssueKind.Infra,
                SearchKind.SearchTimeline,
                searchText: "Notification of assignment to an agent was never received",
                Create("dotnet", "runtime", 35223));
            TriageContextUtil.EnsureTriageIssue(
                TriageIssueKind.Infra,
                SearchKind.SearchTimeline,
                searchText: "Received request to deprovision: The request was cancelled by the remote provider",
                Create("dotnet", "runtime", 34472, includeDefinitions: false),
                Create("dotnet", "core-eng", 9532));
            TriageContextUtil.EnsureTriageIssue(
                TriageIssueKind.Test,
                SearchKind.SearchHelixRunClient,
                searchText: "ERROR.*Job running for too long. Killing...");
            TriageContextUtil.EnsureTriageIssue(
                TriageIssueKind.Test,
                SearchKind.SearchHelixConsole,
                searchText: "after 60000ms waiting for remote process");

            static ModelTriageGitHubIssue Create(string organization, string repository, int number, string? buildQuery = null, bool includeDefinitions = true) =>
                new ModelTriageGitHubIssue()
                {
                    Organization = organization,
                    Repository = repository, 
                    IssueNumber = number,
                    BuildQuery = buildQuery,
                    IncludeDefinitions = includeDefinitions
                };
        }

        public async Task TriageAsync(string projectName, int buildNumber)
        {
            var build = await Server.GetBuildAsync(projectName, buildNumber).ConfigureAwait(false);
            await TriageAsync(build).ConfigureAwait(false);
        }

        public async Task TriageAsync(string buildQuery)
        {
            foreach (var build in await QueryUtil.ListBuildsAsync(buildQuery))
            {
                await TriageAsync(build).ConfigureAwait(false);
            }
        }

        // TODO: need overload that takes builds and groups up the issue and PR updates
        // or maybe just make that a separate operation from triage
        public async Task TriageAsync(Build build)
        {
            var buildInfo = build.GetBuildInfo();
            var modelBuild = TriageContextUtil.EnsureBuild(buildInfo);
            var buildTriageUtil = new BuildTriageUtil(
                build,
                buildInfo,
                modelBuild,
                Server,
                TriageContextUtil,
                Logger);
            await buildTriageUtil.TriageAsync().ConfigureAwait(false);
        }
    }
}
