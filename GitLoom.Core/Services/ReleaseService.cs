using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using GitLoom.Core.Analytics;
using GitLoom.Core.Exceptions;
using GitLoom.Core.Models;
using GitLoom.Core.Releases;
using GitLoom.Core.Security;
using LibGit2Sharp;
using Repository = LibGit2Sharp.Repository;

namespace GitLoom.Core.Services;

/// <summary>
/// Host-agnostic releases service (T-28). Sibling of <see cref="IssueService"/>/<see cref="PullRequestService"/>:
/// it resolves the repo's origin host + stored token and parses <c>owner/repo</c> from the remote through the
/// <b>same shared</b> <see cref="HostConnectionResolver"/> (no duplicate host/token resolver), then dispatches
/// list/create to the matching internal <see cref="IReleaseProvider"/> (GitHub v1; GitLab/Bitbucket/Azure
/// DevOps stubs). <see cref="GenerateNotes"/> is purely local: it walks commits via
/// <see cref="IGitService.ExecuteWithRepo{T}"/> and runs the pure <see cref="ChangelogGenerator"/> — no network.
///
/// <para>The <see cref="HttpClient"/> is <b>shared</b> across every call (never a per-call <c>new</c> — socket
/// exhaustion). SECURITY (G-4): the token is read from the keyring and handed to the provider, which places it
/// only in the <c>Authorization</c> header; it never enters a URL, argv, log, or exception message here.</para>
/// </summary>
public sealed class ReleaseService : IReleaseService
{
    private readonly IGitService _git;
    private readonly HostConnectionResolver _resolver;
    private readonly HttpClient _http;

    /// <param name="httpClient">Optional shared client; tests inject one wrapping a fixture handler.</param>
    public ReleaseService(IGitService git, ISecureKeyring? keyring = null, HttpClient? httpClient = null)
    {
        _git = git ?? throw new ArgumentNullException(nameof(git));
        _resolver = new HostConnectionResolver(git, keyring ?? new SecureKeyring());
        _http = httpClient ?? new HttpClient();
    }

    public bool IsSupported(string repoPath)
    {
        if (!_resolver.TryResolveHost(repoPath, out var host, out var kind, out _))
            return false;
        var provider = ResolveProvider(kind);
        if (provider is not { IsImplemented: true })
            return false;
        return !string.IsNullOrEmpty(_resolver.TokenFor(host));
    }

    public Task<IReadOnlyList<ReleaseItem>> ListAsync(string repoPath, CancellationToken ct)
    {
        var (provider, slug, token) = Resolve(repoPath);
        return provider.ListAsync(slug, token, ct);
    }

    public Task<ReleaseItem> CreateAsync(string repoPath, CreateRelease request, CancellationToken ct)
    {
        var (provider, slug, token) = Resolve(repoPath);
        return provider.CreateAsync(slug, token, request, ct);
    }

    // ---- Local notes generation (no network) --------------------------------------------------

    public string GenerateNotes(string repoPath, string newTag, string targetCommitish)
    {
        return _git.ExecuteWithRepo(repoPath, repo =>
        {
            var target = ResolveCommit(repo, targetCommitish);
            if (target is null) return "";

            // Previous release tag = the highest semver-ish existing tag (name != newTag) that is a
            // strict ancestor of the target. Its commit becomes the "since" boundary of the walk.
            var (prevTagName, prevCommit) = FindPreviousReleaseTag(repo, target, newTag);

            var filter = new CommitFilter
            {
                IncludeReachableFrom = target,
                SortBy = CommitSortStrategies.Topological | CommitSortStrategies.Time,
            };
            if (prevCommit is not null)
                filter.ExcludeReachableFrom = prevCommit;

            var entries = repo.Commits.QueryBy(filter)
                .Select(c => ChangelogGenerator.ParseSubject(c.Sha, FirstLine(c.MessageShort ?? c.Message)))
                .ToList();

            return ChangelogGenerator.BuildNotes(entries, prevTagName, newTag);
        });
    }

    // Resolves a branch/sha/HEAD/tag commitish to its commit; empty → HEAD. Returns null when it can't
    // be resolved (e.g. an unborn HEAD or a bogus ref) so notes generation degrades to empty, never throws.
    private static Commit? ResolveCommit(Repository repo, string commitish)
    {
        var name = string.IsNullOrWhiteSpace(commitish) ? "HEAD" : commitish.Trim();

        var branch = repo.Branches[name];
        if (branch?.Tip is not null) return branch.Tip;

        try
        {
            var obj = repo.Lookup(name);
            if (obj is not null) return obj.Peel<Commit>();
        }
        catch
        {
            // Not a resolvable object — fall through.
        }

        return repo.Head?.Tip;
    }

    // Picks the highest semver-ish tag that is a strict ancestor of the target (excluding a tag named
    // newTag and any tag pointing exactly at the target commit, which would leave an empty range).
    private (string? Name, Commit? Commit) FindPreviousReleaseTag(Repository repo, Commit target, string newTag)
    {
        var candidates = new List<(string Name, Commit Commit, SemVer Version)>();
        foreach (var tag in repo.Tags)
        {
            if (string.Equals(tag.FriendlyName, newTag, StringComparison.Ordinal)) continue;
            var commit = tag.PeeledTarget as Commit ?? tag.Target as Commit;
            if (commit is null) continue;
            if (commit.Sha == target.Sha) continue;                       // same commit → no range

            // Ancestor test: the merge base of an ancestor and the target IS the ancestor itself.
            var mergeBase = repo.ObjectDatabase.FindMergeBase(commit, target);
            if (mergeBase is null || mergeBase.Sha != commit.Sha) continue;

            candidates.Add((tag.FriendlyName, commit, SemVer.Parse(tag.FriendlyName)));
        }

        if (candidates.Count == 0) return (null, null);

        var best = candidates
            .OrderByDescending(c => c.Version)
            .ThenByDescending(c => c.Commit.Committer.When)
            .First();
        return (best.Name, best.Commit);
    }

    private static string FirstLine(string message)
    {
        if (string.IsNullOrEmpty(message)) return "";
        var idx = message.IndexOfAny(new[] { '\r', '\n' });
        return (idx < 0 ? message : message.Substring(0, idx)).Trim();
    }

    // ---- Dispatch -----------------------------------------------------------------------------

    // Resolves (provider, owner/repo, token) or throws a typed error. Central so no operation reaches a
    // provider without a validated host, parsed slug, and a token. Host/token/slug plumbing is the shared
    // HostConnectionResolver (same path as IssueService/PullRequestService); provider dispatch is release-specific.
    private (IReleaseProvider Provider, RepoSlug Slug, string Token) Resolve(string repoPath)
    {
        if (!_resolver.TryResolveHost(repoPath, out var host, out var kind, out var remoteUrl))
            throw new GitOperationException("This repository has no origin remote pointing at a supported host.");

        var provider = ResolveProvider(kind);
        if (provider is not { IsImplemented: true })
            throw new GitOperationException($"Releases are not available for '{host}'.");

        var slug = HostConnectionResolver.ParseSlug(remoteUrl)
            ?? throw new GitOperationException($"Could not parse an owner/repository from the origin URL for '{host}'.");

        var token = _resolver.TokenFor(host);
        if (string.IsNullOrEmpty(token))
            throw new AuthenticationRequiredException($"No stored token for {host}. Sign in to continue.", host);

        return (provider, slug, token);
    }

    // Dispatch table (additive): a real provider per implemented host, a typed stub otherwise.
    private IReleaseProvider? ResolveProvider(HostKind kind) => kind switch
    {
        HostKind.GitHub => new GitHubReleaseProvider(_http),
        HostKind.GitLab => new GitLabReleaseProvider(),
        HostKind.Bitbucket => new BitbucketReleaseProvider(),
        HostKind.AzureDevOps => new AzureDevOpsReleaseProvider(),
        _ => null,
    };

    // ---- Semver-ish tag ordering --------------------------------------------------------------

    // A tolerant version parse for tag ordering: strips a leading 'v'/'V', reads up to three dotted
    // numeric components (major.minor.patch), and treats a trailing pre-release suffix (e.g. "-rc1") as
    // lower than the same release. Non-numeric / unparsable tags sort below any parsable one so a real
    // "v1.2.0" always beats a "nightly" tag when choosing the previous release.
    private readonly struct SemVer : IComparable<SemVer>
    {
        private readonly bool _parsed;
        private readonly int _major, _minor, _patch;
        private readonly bool _hasPre;

        private SemVer(bool parsed, int major, int minor, int patch, bool hasPre)
        {
            _parsed = parsed; _major = major; _minor = minor; _patch = patch; _hasPre = hasPre;
        }

        private static readonly Regex Rx = new(
            @"^[vV]?(?<maj>\d+)(\.(?<min>\d+))?(\.(?<pat>\d+))?(?<pre>[-+].*)?$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public static SemVer Parse(string tag)
        {
            var m = Rx.Match(tag ?? "");
            if (!m.Success) return new SemVer(false, 0, 0, 0, false);
            int Num(string g) => m.Groups[g].Success ? int.Parse(m.Groups[g].Value) : 0;
            return new SemVer(true, Num("maj"), Num("min"), Num("pat"), m.Groups["pre"].Success);
        }

        public int CompareTo(SemVer other)
        {
            if (_parsed != other._parsed) return _parsed ? 1 : -1;
            if (!_parsed) return 0;
            var c = _major.CompareTo(other._major); if (c != 0) return c;
            c = _minor.CompareTo(other._minor); if (c != 0) return c;
            c = _patch.CompareTo(other._patch); if (c != 0) return c;
            // A release outranks its own pre-releases (1.2.0 > 1.2.0-rc1).
            return (!_hasPre).CompareTo(!other._hasPre);
        }
    }
}
