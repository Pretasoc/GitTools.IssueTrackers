﻿namespace GitTools.IssueTrackers.Jira
{
    using GitTools.IssueTrackers.Logging;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Jira = Atlassian.Jira;
    using Version = Version;

    public class JiraIssueTracker : IIssueTracker
    {
        private static readonly ILog Log = LogProvider.GetCurrentClassLogger();

        private static readonly HashSet<string> KnownClosedStatuses = new HashSet<string>(new[] { "resolved", "closed", "done" });
        private static readonly HashSet<string> AcceptedResolutions = new HashSet<string>(new[] { "fixed" });

        private readonly string _project;
        private readonly string _server;
        private readonly AuthSettings _authenticationInfo;

        public JiraIssueTracker(string server, string project, AuthSettings authenticationInfo)
        {
            _server = server;
            _project = project;
            _authenticationInfo = authenticationInfo ?? new AuthSettings();
        }

        public async Task<IEnumerable<Issue>> GetIssuesAsync(IssueTrackerFilter filter)
        {
            Log.DebugFormat("Connecting to Jira server '{0}'", _server);

            var jira = Atlassian.Jira.Jira.CreateRestClient(_server, _authenticationInfo.Username,
                _authenticationInfo.Password, new Jira.JiraRestClientSettings());

            var jiraRestClient = jira.RestClient;

            Log.Debug("Retrieving statuses");

           
            var statuses = (await jira.Statuses.GetStatusesAsync()).ToList();
            var resolutions = (await jira.Resolutions.GetResolutionsAsync()).ToList();

            var openedStatuses = GetOpenedStatuses(statuses);
            var closedStatuses = GetClosedStatuses(statuses);
            var acceptedResolutions = GetAcceptedResolutions(resolutions);

            var finalFilter = PrepareFilter(filter, openedStatuses, closedStatuses, acceptedResolutions);

            var issues = new List<Issue>();

            Log.DebugFormat("Searching for issues using filter '{0}'", filter);

            const int MaxIssues = 200;

            // TODO: Once the Atlassian.Sdk issue type contains all info, remove custom JiraIssue
            var retrievedIssues = await jiraRestClient.GetIssues(finalFilter, 0, MaxIssues);
            //var retrievedIssues = await jiraRestClient.GetIssuesFromJqlAsync(finalFilter, MaxIssues, 0, CancellationToken.None);

            int lastRetrievedIssuesCount = retrievedIssues.Count;

            while (lastRetrievedIssuesCount % MaxIssues == 0)
            {
                var newlyRetrievedIssues = await jiraRestClient.GetIssues(finalFilter, lastRetrievedIssuesCount, MaxIssues);
                //var newlyRetrievedIssues = await jiraRestClient.GetIssuesFromJqlAsync(finalFilter, MaxIssues, lastRetrievedIssuesCount, CancellationToken.None);
                if (newlyRetrievedIssues.Count == 0)
                {
                    break;
                }

                retrievedIssues.AddRange(newlyRetrievedIssues);

                lastRetrievedIssuesCount = retrievedIssues.Count;
            }

            foreach (var issue in retrievedIssues)
            {
                var gitIssue = new Issue(issue.key)
                {
                    IssueType = IssueType.Issue,
                    Title = issue.summary,
                    Description = issue.description,
                    DateCreated = issue.created,
                    Labels = issue.labels.ToArray()
                };

                if (closedStatuses.Any(x => string.Equals(x.Id, issue.status)))
                {
                    gitIssue.DateClosed = issue.resolutionDate;
                }

                foreach (var fixVersion in issue.fixVersions)
                {
                    gitIssue.FixVersions.Add(new Version
                    {
                        Name = fixVersion.name,
                        ReleaseDate = fixVersion.releaseDate,
                        IsReleased = fixVersion.released
                    });
                }

                var uri = new Uri(new Uri(_server), string.Format("/browse/{0}", issue.key));
                gitIssue.Url = uri.ToString();

                issues.Add(gitIssue);
            }

            Log.DebugFormat("Found '{0}' issues using filter '{1}'", issues.Count, filter);

            return issues.AsEnumerable();
        }

        private IEnumerable<Jira.IssueResolution> GetAcceptedResolutions(List<Jira.IssueResolution> resolutions)
        {
            var acceptedResolutions = new List<Jira.IssueResolution>();

            foreach (var resolution in resolutions)
            {
                if (AcceptedResolutions.Contains(resolution.Name.ToLower()))
                {
                    acceptedResolutions.Add(resolution);
                }
            }

            return acceptedResolutions;
        }

        private string PrepareFilter(IssueTrackerFilter filter, IEnumerable<Jira.IssueStatus> openedStatuses, 
            IEnumerable<Jira.IssueStatus> closedStatuses, IEnumerable<Jira.IssueResolution> acceptedResolutions)
        {
            var finalFilter = string.Empty;
            if (!string.IsNullOrWhiteSpace(filter.Filter))
            {
                finalFilter = filter.Filter;
            }

            if (filter.IncludeOpen && filter.IncludeClosed)
            {
                // no need to filter anything
            }
            else if (!filter.IncludeOpen && !filter.IncludeClosed)
            {
                throw new Exception("Cannot exclude both open and closed issues, nothing will be returned");
            }
            else if (filter.IncludeOpen)
            {
                finalFilter = finalFilter.AddJiraFilter(string.Format("status in ({0})", string.Join(", ", openedStatuses.Select(x => string.Format("\"{0}\"", x)))));
            }
            else if (filter.IncludeClosed)
            {
                finalFilter = finalFilter.AddJiraFilter(string.Format("status in ({0})", string.Join(", ", closedStatuses.Select(x => string.Format("\"{0}\"", x)))));
            }

            if (filter.Since.HasValue)
            {
                finalFilter = finalFilter.AddJiraFilter(string.Format("resolutiondate >= '{0}'", filter.Since.Value.ToString("yyyy-MM-dd")));
            }

            if (acceptedResolutions.Any())
            {
                finalFilter = finalFilter.AddJiraFilter(string.Format("resolution in ({0})", string.Join(", ", acceptedResolutions.Select(x => string.Format("\"{0}\"", x)))));
            }

            finalFilter = finalFilter.AddJiraFilter(string.Format("project = {0}", _project));

            return finalFilter;
        }

        private List<Jira.IssueStatus> GetOpenedStatuses(List<Jira.IssueStatus> statuses)
        {
            return (from issueStatus in statuses
                    where !KnownClosedStatuses.Contains(issueStatus.Name.ToLower())
                    select issueStatus).ToList();
        }

        private List<Jira.IssueStatus> GetClosedStatuses(List<Jira.IssueStatus> statuses)
        {
            return (from issueStatus in statuses
                    where KnownClosedStatuses.Contains(issueStatus.Name.ToLower())
                    select issueStatus).ToList();
        }

        public static IIssueTracker Factory(string url, string project, AuthSettings authentication)
        {
            return new JiraIssueTracker(url, project, authentication);
        }

        public static bool TryCreate(string url, string project, AuthSettings authentication, out IIssueTracker issueTracker)
        {
            // For now just check if it contains atlassian.net
            if (!string.IsNullOrWhiteSpace(url))
            {
                if (url.ToLower().Contains(".atlassian."))
                {
                    issueTracker = new JiraIssueTracker(url, project, authentication);
                    return true;
                }
            }

            issueTracker = null;
            return false;
        }
    }
}