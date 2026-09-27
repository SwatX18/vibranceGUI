using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace vibrance.GUI.common
{
    /// <summary>
    /// Covers every part of the update check that can be decided without a network: the version
    /// parse and comparison, the throttle, the notification wording, and the JSON contract.
    ///
    /// Nothing here opens a socket. The one check that exercises the GitHub payload shape feeds
    /// GitHubReleaseSource.ReadRelease a MemoryStream, and the check that proves the default
    /// source is offline is the reason a suite run on a build agent, or under the reflection
    /// harness, never talks to api.github.com at all.
    /// </summary>
    public class UpdateCheckFixture
    {
        /// <summary>
        /// An IReleaseSource that records that it was asked and returns whatever it was given -
        /// the fixture-owned fake, in the same spirit as ProfileToggleFixture's RecordingLogSink.
        /// </summary>
        private class StubReleaseSource : IReleaseSource
        {
            private readonly ReleaseInfo _release;
            public int Calls { get; private set; }

            public StubReleaseSource(ReleaseInfo release)
            {
                _release = release;
            }

            public ReleaseInfo TryGetLatestRelease()
            {
                Calls++;
                return _release;
            }
        }

        public static List<string> Run()
        {
            List<string> lines = new List<string>();
            int passed = 0;
            int total = 0;

            Action<string, bool> check = (label, ok) =>
            {
                total++;
                if (ok)
                    passed++;
                lines.Add(string.Format("[{0}] {1}", ok ? "PASS" : "FAIL", label));
            };

            lines.Add("Version parsing - the tags in this repository are spelled \"v2.10.2\":");

            Version parsed;
            check("\"v2.10.2\" parses, and the leading v is dropped",
                UpdateCheckPolicy.TryParseVersion("v2.10.2", out parsed) && parsed.Major == 2 &&
                parsed.Minor == 10 && parsed.Build == 2);
            check("\"2.10.2\" parses without a v",
                UpdateCheckPolicy.TryParseVersion("2.10.2", out parsed) && parsed.Minor == 10);
            check("surrounding whitespace is tolerated",
                UpdateCheckPolicy.TryParseVersion("  v2.10.2 ", out parsed) && parsed.Build == 2);
            check("\"V2.10.2\" parses - the tag could be spelled either way",
                UpdateCheckPolicy.TryParseVersion("V2.10.2", out parsed));
            check("garbage is rejected rather than guessed at",
                !UpdateCheckPolicy.TryParseVersion("latest", out parsed) &&
                !UpdateCheckPolicy.TryParseVersion("", out parsed) &&
                !UpdateCheckPolicy.TryParseVersion(null, out parsed) &&
                !UpdateCheckPolicy.TryParseVersion("v", out parsed));

            lines.Add(string.Empty);
            lines.Add("Version comparison - the trap a string compare walks straight into:");

            // "2.9.0" sorts AFTER "2.10.2" lexically. A string comparison here would have looked
            // correct through 2.10.x and silently stopped announcing anything at the next bump.
            Version v290, v2102;
            UpdateCheckPolicy.TryParseVersion("2.9.0", out v290);
            UpdateCheckPolicy.TryParseVersion("v2.10.2", out v2102);
            check("2.10.2 is newer than 2.9.0 (string compare says the opposite)",
                UpdateCheckPolicy.Compare(v290, v2102) == UpdateAvailability.UpdateAvailable);
            check("and the string compare really would have said the opposite",
                string.CompareOrdinal("2.9.0", "2.10.2") > 0);
            check("2.9.0 against itself is up to date",
                UpdateCheckPolicy.Compare(v290, v290) == UpdateAvailability.UpToDate);
            check("a newer local build than the newest release is up to date, not an update",
                UpdateCheckPolicy.Compare(v2102, v290) == UpdateAvailability.UpToDate);
            check("a null on either side is Unknown, never an update prompt",
                UpdateCheckPolicy.Compare(null, v2102) == UpdateAvailability.Unknown &&
                UpdateCheckPolicy.Compare(v290, null) == UpdateAvailability.Unknown);

            // Version leaves absent components at -1, and treats -1 as less than 0, so an
            // unnormalised Parse("2.10") < Parse("2.10.0") - the same release written two ways
            // would announce itself as an update forever.
            Version short210, long2100;
            UpdateCheckPolicy.TryParseVersion("2.10", out short210);
            UpdateCheckPolicy.TryParseVersion("2.10.0.0", out long2100);
            check("\"2.10\" and \"2.10.0.0\" compare equal - the same release written two ways is not an update",
                UpdateCheckPolicy.Compare(short210, long2100) == UpdateAvailability.UpToDate &&
                UpdateCheckPolicy.Compare(long2100, short210) == UpdateAvailability.UpToDate);
            check("and the unnormalised comparison really would have differed",
                new Version("2.10") < new Version("2.10.0"));

            lines.Add(string.Empty);
            lines.Add("The throttle - GitHub allows 60 unauthenticated requests an hour per IP:");

            DateTime now = new DateTime(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);
            check("disabled means no check, however long it has been",
                !UpdateCheckPolicy.ShouldCheckNow(false, default(DateTime), now) &&
                !UpdateCheckPolicy.ShouldCheckNow(false, now.AddYears(-1), now));
            check("never checked before is due",
                UpdateCheckPolicy.ShouldCheckNow(true, default(DateTime), now));
            check("checked a minute ago is not due - a dozen restarts while testing games ask once",
                !UpdateCheckPolicy.ShouldCheckNow(true, now.AddMinutes(-1), now));
            check("checked just inside the interval is not due",
                !UpdateCheckPolicy.ShouldCheckNow(true, now - UpdateCheckPolicy.MinimumInterval.Add(TimeSpan.FromMinutes(-1)), now));
            check("checked exactly the interval ago is due",
                UpdateCheckPolicy.ShouldCheckNow(true, now - UpdateCheckPolicy.MinimumInterval, now));
            check("checked well over the interval ago is due",
                UpdateCheckPolicy.ShouldCheckNow(true, now.AddDays(-3), now));
            // A stamp in the future comes from a clock corrected backwards or a settings file
            // copied off another machine. Treating it as "not due" would lock the check out until
            // real time caught up, which could be months.
            check("a stamp in the future is due, not a lockout until the clock catches up",
                UpdateCheckPolicy.ShouldCheckNow(true, now.AddDays(5), now));

            lines.Add(string.Empty);
            lines.Add("Notification wording:");

            Version v2100, v2101;
            UpdateCheckPolicy.TryParseVersion("2.10.0", out v2100);
            UpdateCheckPolicy.TryParseVersion("2.10.1", out v2101);

            string x64FromBroken = UpdateCheckPolicy.BuildNotificationText(v2100, v2102, true);
            string x86FromBroken = UpdateCheckPolicy.BuildNotificationText(v2100, v2102, false);
            string x64FromFixed = UpdateCheckPolicy.BuildNotificationText(v2101, v2102, true);

            check("both versions appear, short form, no trailing \".0\"",
                x64FromBroken.Contains("2.10.2") && x64FromBroken.Contains("2.10.0") &&
                !x64FromBroken.Contains("2.10.2.0"));
            check("an x64 build older than 2.10.1 is told its NVIDIA detection was broken",
                x64FromBroken.Contains("could not detect NVIDIA"));
            check("an x86 build of the same version is not - x86 was never affected",
                !x86FromBroken.Contains("could not detect NVIDIA"));
            check("an x64 build on 2.10.1 or newer is not told it is broken either",
                !x64FromFixed.Contains("could not detect NVIDIA"));
            check("every message says what clicking does",
                x64FromBroken.Contains("Click to open") && x86FromBroken.Contains("Click to open") &&
                x64FromFixed.Contains("Click to open"));

            // Three components always: a zero Build is a real part of the version here
            // ("v2.10.0" is a tag that shipped), and only the fourth is noise.
            check("FormatVersion keeps the patch component and drops only an unset revision",
                UpdateCheckPolicy.FormatVersion(new Version(2, 10, 2, 0)) == "2.10.2" &&
                UpdateCheckPolicy.FormatVersion(new Version(2, 10, 0, 0)) == "2.10.0" &&
                UpdateCheckPolicy.FormatVersion(new Version(2, 9, 0, 0)) == "2.9.0" &&
                UpdateCheckPolicy.FormatVersion(new Version(2, 10, 2, 3)) == "2.10.2.3");

            lines.Add(string.Empty);
            lines.Add("The GitHub payload contract, fed from memory rather than from the network:");

            // "html_url" appears on the author object as well as on the release. A regex or an
            // IndexOf would take whichever came first; the declared contract takes the top-level
            // one, which is the release page.
            const string payload =
                "{\"url\":\"https://api.github.com/x\"," +
                "\"author\":{\"login\":\"someone\",\"html_url\":\"https://github.com/someone\"}," +
                "\"tag_name\":\"v2.10.2\"," +
                "\"draft\":false,\"prerelease\":false," +
                "\"html_url\":\"https://github.com/SwatX18/vibranceGUI/releases/tag/v2.10.2\"}";
            ReleaseInfo fromJson = ReadFrom(payload);
            check("tag_name and the release's own html_url are read, not the author's",
                fromJson != null && fromJson.Version.Minor == 10 && fromJson.Version.Build == 2 &&
                fromJson.HtmlUrl == "https://github.com/SwatX18/vibranceGUI/releases/tag/v2.10.2");

            check("a draft is ignored",
                ReadFrom(payload.Replace("\"draft\":false", "\"draft\":true")) == null);
            check("a prerelease is ignored",
                ReadFrom(payload.Replace("\"prerelease\":false", "\"prerelease\":true")) == null);
            check("an unparseable tag yields nothing rather than a bad comparison",
                ReadFrom(payload.Replace("\"v2.10.2\"", "\"nightly\"")) == null);

            lines.Add(string.Empty);
            lines.Add("The seam - why a suite run makes no outbound requests:");

            check("the default release source is the offline one",
                ReleaseSource.Current is OfflineReleaseSource);
            check("the offline source returns nothing and cannot reach the network",
                new OfflineReleaseSource().TryGetLatestRelease() == null);

            IReleaseSource saved = ReleaseSource.Current;
            try
            {
                Version stubVersion;
                UpdateCheckPolicy.TryParseVersion("v9.9.9", out stubVersion);
                StubReleaseSource stub = new StubReleaseSource(new ReleaseInfo(stubVersion, "v9.9.9", "https://example.invalid"));
                ReleaseSource.Current = stub;
                ReleaseInfo got = ReleaseSource.Current.TryGetLatestRelease();
                check("Current is swappable, so a check can drive a fake end to end",
                    got != null && got.TagName == "v9.9.9" && stub.Calls == 1);

                ReleaseSource.Current = null;
                check("assigning null falls back to the offline source rather than NullReferencing at startup",
                    ReleaseSource.Current is OfflineReleaseSource);
            }
            finally
            {
                ReleaseSource.Current = saved;
            }
            check("the original source is restored afterwards",
                ReleaseSource.Current is OfflineReleaseSource);

            check("the production URL points at this fork's releases",
                GitHubReleaseSource.LatestReleaseUrl.StartsWith("https://") &&
                GitHubReleaseSource.LatestReleaseUrl.Contains("SwatX18/vibranceGUI") &&
                GitHubReleaseSource.LatestReleaseUrl.EndsWith("/releases/latest"));

            lines.Add(string.Empty);
            lines.Add(string.Format("PASSED {0}/{1}", passed, total));
            lines.Add(string.Empty);
            lines.Add("No check here opens a socket, reads the settings file or touches the clock:");
            lines.Add("every input is passed in, so this answers the same on a build agent as on a");
            lines.Add("machine with no network at all.");
            return lines;
        }

        private static ReleaseInfo ReadFrom(string json)
        {
            using (MemoryStream stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
            {
                return GitHubReleaseSource.ReadRelease(stream);
            }
        }
    }
}
