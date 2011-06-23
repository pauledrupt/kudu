﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Kudu.Core.Infrastructure;

namespace Kudu.Core.Git {
    /// <summary>
    /// Implementation of a git repository over git.exe
    /// </summary>
    public class GitExeRepository : IRepository {
        private readonly Executable _gitExe;

        public GitExeRepository(string path)
            : this(ResolveGitPath(), path) {

        }

        public GitExeRepository(string pathToGitExe, string path) {
            _gitExe = new Executable(pathToGitExe, path);
        }

        public string CurrentId {
            get {
                var head = Head;
                if (head != null) {
                    return head.Id;
                }
                return null;
            }
        }

        private ChangeSet Head {
            get {
                return Log(1, all: false).SingleOrDefault();
            }
        }

        public void Initialize() {
            _gitExe.Execute("init");
            _gitExe.Execute("config core.autocrlf true");
        }

        public IEnumerable<FileStatus> GetStatus() {
            string status = _gitExe.Execute("status --porcelain");
            return ParseStatus(status.AsReader());
        }

        public IEnumerable<ChangeSet> GetChanges() {
            return Log();
        }

        public void AddFile(string path) {
            _gitExe.Execute("add {0}", path);
        }

        public void RemoveFile(string path) {
            _gitExe.Execute("rm {0} --cached", path);
        }

        public ChangeSet Commit(string authorName, string message) {
            // Add all unstaged files
            _gitExe.Execute("add .");
            _gitExe.Execute("commit -m\"{0}\" --author=\"{1}\"", message, authorName);

            return Head;
        }

        public void Update(string id) {
            _gitExe.Execute("checkout {0} --force", id);
        }

        public ChangeSetDetail GetDetails(string id) {
            string show = _gitExe.Execute("show {0} -m -p --numstat", id);
            return ParseShow(show.AsReader());
        }

        private bool IsEmpty() {
            // REVIEW: Is this reliable
            return String.IsNullOrWhiteSpace(_gitExe.Execute("branch"));
        }

        private static string ResolveGitPath() {
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string path = Path.Combine(programFiles, "Git", "bin", "git.exe");

            if (!File.Exists(path)) {
                throw new InvalidOperationException("Unable to locate git.exe");
            }

            return path;
        }

        private IEnumerable<ChangeSet> Log(int? limit = null, bool all = true) {
            if (IsEmpty()) {
                return Enumerable.Empty<ChangeSet>();
            }
            string command = "log";
            if (limit != null) {
                command += " -" + limit;
            }

            if (all) {
                command += " --all";
            }
            string log = _gitExe.Execute(command);
            return ParseCommits(log.AsReader());
        }

        private IEnumerable<ChangeSet> ParseCommits(IStringReader reader) {
            do {
                yield return ParseCommit(reader);
            } while (!reader.Done);
        }

        private bool IsCommitHeader(string value) {
            return value.StartsWith("commit ");
        }

        private ChangeSetDetail ParseShow(IStringReader reader) {
            var detail = ParseCommitAndSummary(reader);

            foreach (var diff in ParseDiff(reader)) {
                detail.Diffs.Add(diff);
            }

            return detail;
        }

        private ChangeSetDetail ParseCommitAndSummary(IStringReader reader) {
            // Parse the changeset
            ChangeSet changeSet = ParseCommit(reader);

            var detail = new ChangeSetDetail(changeSet);

            ParseSummary(reader, detail);

            return detail;
        }

        private void ParseSummary(IStringReader reader, ChangeSetDetail detail) {
            while (!reader.Done) {
                string line = reader.ReadLine();

                if (IsSingleLineFeed(line)) {
                    break;
                }
                else if (line.Contains('\t')) {
                    // n	n	path
                    string[] parts = line.Split('\t');
                    int insertions;
                    Int32.TryParse(parts[0], out insertions);
                    int deletions;
                    Int32.TryParse(parts[1], out deletions);
                    string path = parts[2].Trim();

                    detail.FileStats[path] = new FileStats {
                        Insertions = insertions,
                        Deletions = deletions,
                        Binary = parts[0] == "-" && parts[1] == "-"
                    };
                }
                else {
                    // n files changed, n insertions(+), n deletions(-)
                    var subReader = line.AsReader();
                    subReader.SkipWhitespace();
                    detail.FilesChanged = subReader.ReadInt();
                    subReader.ReadUntil(',');
                    subReader.Skip(1);
                    subReader.SkipWhitespace();
                    detail.Insertions = subReader.ReadInt();
                    subReader.ReadUntil(',');
                    subReader.Skip(1);
                    subReader.SkipWhitespace();
                    detail.Deletions = subReader.ReadInt();
                }
            }
        }

        private IEnumerable<FileDiff> ParseDiff(IStringReader reader) {
            var builder = new StringBuilder();

            // If this was a merge change set then we'll parse the details out of the
            // first diff
            ChangeSetDetail merge = null;

            do {
                string line = reader.ReadLine();

                // If we see a new diff header then process the previous diff if any
                if ((reader.Done || IsDiffHeader(line)) && builder.Length > 0) {
                    if (reader.Done) {
                        builder.Append(line);
                    }
                    string diffChunk = builder.ToString();
                    FileDiff diff = ParseDiffChunk(diffChunk.AsReader(), ref merge);

                    if (diff != null) {
                        yield return diff;
                    }

                    builder.Clear();
                }

                if (!reader.Done) {
                    builder.Append(line);
                }
            } while (!reader.Done);
        }

        private bool IsDiffHeader(string line) {
            return line.StartsWith("diff --git", StringComparison.InvariantCulture);
        }

        private FileDiff ParseDiffChunk(IStringReader reader, ref ChangeSetDetail merge) {
            // Extract the file name from the diff header
            var headerReader = reader.ReadLine().AsReader();
            headerReader.ReadUntil("a/");
            headerReader.Skip(2);
            string fileName = headerReader.ReadUntilWhitespace();

            // Skip files from merged changesets
            if (merge != null && merge.FileStats.ContainsKey(fileName)) {
                return null;
            }

            string result;
            var diff = new FileDiff(fileName);

            // Parse the diff
            if (reader.TryReadUntil("@@", out result)) {
                while (!reader.Done) {
                    string line = reader.ReadLine();
                    if (line.StartsWith("+")) {
                        diff.Lines.Add(new LineDiff(ChangeType.Added, line));
                    }
                    else if (line.StartsWith("-")) {
                        diff.Lines.Add(new LineDiff(ChangeType.Deleted, line));
                    }
                    else if (IsCommitHeader(line)) {
                        reader.PutBack(line.Length);
                        merge = ParseCommitAndSummary(reader);
                    }
                    else {
                        diff.Lines.Add(new LineDiff(ChangeType.None, line));
                    }
                }
            }
            return diff;
        }

        private ChangeSet ParseCommit(IStringReader reader) {
            // commit hash
            reader.ReadUntilWhitespace();
            reader.SkipWhitespace();
            string id = reader.ReadUntilWhitespace();

            // Merges will have (from hash) so we're skipping that
            reader.ReadLine();

            string author = null;
            string email = null;
            string date = null;

            while (!reader.Done) {
                string line = reader.ReadLine();

                if (IsSingleLineFeed(line)) {
                    break;
                }

                var subReader = line.AsReader();
                string key = subReader.ReadUntil(':');
                // Skip :
                subReader.Skip();
                subReader.SkipWhitespace();
                string value = subReader.ReadToEnd().Trim();

                if (key.Equals("Author", StringComparison.OrdinalIgnoreCase)) {
                    // Author <email>
                    var authorReader = value.AsReader();
                    author = authorReader.ReadUntil('<').Trim();
                    authorReader.Skip();
                    email = authorReader.ReadUntil('>');
                }
                else if (key.Equals("Date", StringComparison.OrdinalIgnoreCase)) {
                    date = value;
                }
            }

            var messageBuilder = new StringBuilder();
            while (!reader.Done) {
                string line = reader.ReadLine();

                if (IsSingleLineFeed(line)) {
                    break;
                }
                messageBuilder.Append(line);
            }

            string message = messageBuilder.ToString();
            return new ChangeSet(id, author, message, DateTimeOffset.ParseExact(date, "ddd MMM d HH:mm:ss yyyy zzz", CultureInfo.InvariantCulture));
        }

        private bool IsSingleLineFeed(string value) {
            return value.Length == 1 && value[0] == '\n';
        }

        private IEnumerable<FileStatus> ParseStatus(IStringReader reader) {
            while (!reader.Done) {
                var subReader = reader.ReadLine().AsReader();
                string status = subReader.ReadUntilWhitespace().Trim();
                string path = subReader.ReadLine().Trim();
                yield return new FileStatus(path, Convert(status));
            }
        }

        private ChangeType Convert(string status) {
            switch (status) {
                case "A":
                case "AM":
                    return ChangeType.Added;
                case "M":
                    return ChangeType.Modified;
                case "D":
                    return ChangeType.Deleted;
                case "R":
                    return ChangeType.Renamed;
                case "??":
                    return ChangeType.Untracked;
                default:
                    break;
            }

            throw new InvalidOperationException("Unsupported status " + status);
        }
    }
}