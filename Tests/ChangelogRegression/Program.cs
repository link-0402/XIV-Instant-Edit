using System.Text.RegularExpressions;
using InstantEdit;
using InstantEdit.Ui;
using InstantEdit.TestSupport;
using Newtonsoft.Json;
using static InstantEdit.TestSupport.Assertions;

Require(Regex.IsMatch(ChangelogCatalog.CurrentVersion, @"^\d+\.\d+\.\d+$"), "the changelog current version is a well-formed release number");
Require(ChangelogCatalog.HasUniqueVersions(), "changelog release versions are unique");
Require(ChangelogCatalog.IsOrderedNewestFirst(), "changelog releases are newest-first");
Require(ChangelogCatalog.Releases[0].Version == ChangelogCatalog.CurrentVersion, "the current release is first");
Require(ChangelogCatalog.Releases.Any(release => release.Version == "1.0.0"), "the initial release is present");
Require(ChangelogCatalog.Releases.Any(release => release.Version == ChangelogCatalog.CurrentVersion), "the current release is present");
Require(!ChangelogCatalog.Releases.Any(release => release.Version is "1.0.2" or "1.0.3"), "nonexistent releases are omitted");

Require(ChangelogCatalog.ShouldAutoOpen(null, ChangelogCatalog.CurrentVersion), "first launch opens the changelog");
Require(ChangelogCatalog.ShouldAutoOpen("1.1.7", ChangelogCatalog.CurrentVersion), "an older version opens the changelog");
Require(!ChangelogCatalog.ShouldAutoOpen(ChangelogCatalog.CurrentVersion, ChangelogCatalog.CurrentVersion), "the current version does not reopen automatically");
Require(!ChangelogCatalog.ShouldAutoOpen("", ""), "an unknown current version does not auto-open");

var saved = new Configuration { LastSeenChangelogVersion = ChangelogCatalog.CurrentVersion };
var roundTrip = JsonConvert.DeserializeObject<Configuration>(JsonConvert.SerializeObject(saved));
Require(roundTrip?.LastSeenChangelogVersion == ChangelogCatalog.CurrentVersion, "last-seen changelog version round-trips");

var legacy = JsonConvert.DeserializeObject<Configuration>("{\"Version\":10}");
Require(legacy?.LastSeenChangelogVersion == "", "legacy configuration defaults the last-seen version");

Console.WriteLine("Changelog regression checks passed.");
