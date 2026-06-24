using LibGit2Sharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace GitLoom.Core.Analytics
{
    public class RepositoryAnalyzer
    {
        public Task<Dictionary<LanguageModel, long>> CalculateLanguageBreakdownAsync(string repositoryPath)
        {
            return Task.Run(() =>
            {
                var languageBytes = new Dictionary<LanguageModel, long>();

                using var repo = new Repository(repositoryPath);
                
                // Get the HEAD commit's tree
                var commit = repo.Head.Tip;
                if (commit == null) return languageBytes;
                
                var tree = commit.Tree;

                // Recursively traverse the tree, ignoring gitignore paths implicitly since they aren't in the tree
                TraverseTree(repo, tree, languageBytes);

                return languageBytes;
            });
        }

        private void TraverseTree(Repository repo, Tree tree, Dictionary<LanguageModel, long> languageBytes)
        {
            foreach (var treeEntry in tree)
            {
                if (treeEntry.TargetType == TreeEntryTargetType.Tree)
                {
                    // It's a directory, traverse it
                    var subTree = (Tree)repo.Lookup(treeEntry.Target.Id);
                    TraverseTree(repo, subTree, languageBytes);
                }
                else if (treeEntry.TargetType == TreeEntryTargetType.Blob)
                {
                    // It's a file
                    string extension = Path.GetExtension(treeEntry.Name);
                    var lang = LanguageRegistry.GetLanguageByExtension(extension);
                    if (lang != null)
                    {
                        var blob = (Blob)treeEntry.Target;
                        if (!languageBytes.ContainsKey(lang))
                        {
                            languageBytes[lang] = 0;
                        }
                        languageBytes[lang] += blob.Size;
                    }
                }
            }
        }

        public Task<PunchCardStats> GeneratePunchCardAsync(string repositoryPath)
        {
            return Task.Run(() =>
            {
                var stats = new PunchCardStats();

                using var repo = new Repository(repositoryPath);
                
                // Traverse history
                var commits = repo.Commits.Take(10000); // Limit to last 10,000 commits for performance
                foreach (var commit in commits)
                {
                    stats.AddCommit(commit.Author.When);
                }

                return stats;
            });
        }
    }
}
