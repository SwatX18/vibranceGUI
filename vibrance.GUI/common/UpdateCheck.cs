using System;
using System.IO;
using System.Net;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace vibrance.GUI.common
{
    /// <summary>
    /// What a completed check concluded. Unknown is not a failure to report to the user - no
    /// network, a proxy, a rate limit and a garbled tag all land here, and all of them mean
    /// "say nothing", never "show an error". An update check that nags about its own failure is
    /// worse than one that does not run.
    /// </summary>
    public enum UpdateAvailability
    {
        Unknown = 0,
        UpToDate = 1,
        UpdateAvailable = 2
    }

    /// <summary>
    /// The two fields of a GitHub release this app cares about. Everything else in that JSON -
    /// the author, the asset list, the body - is deliberately not modelled.
    /// </summary>
    public class ReleaseInfo
    {
        public ReleaseInfo(Version version, string tagName, string htmlUrl)
        {
            Version = version;
            TagName = tagName;
            HtmlUrl = htmlUrl;
        }

        public Version Version { get; private set; }
        public string TagName { get; private set; }
        public string HtmlUrl { get; private set; }
    }

    /// <summary>
    /// The seam over "ask the internet what the newest release is" - same shape as ILogSink and
    /// IHdrStateReader. Implementations must never throw: a checker that can take down startup is
    /// strictly worse than no checker, and this is the first code in vibranceGUI's history that
    /// talks to anything off the machine.
    /// </summary>
    public interface IReleaseSource
    {
        /// <summary>
        /// The newest published release, or null for every failure mode there is. Never throws.
        /// </summary>
        ReleaseInfo TryGetLatestRelease();
    }

    /// <summary>
    /// The default IReleaseSource (see ReleaseSource._current), and the reason the fixtures can
    /// run on a build agent with no network and no surprise HTTP traffic.
    ///
    /// This defaults to "do nothing" for exactly the reason LogSink defaults to NullLogSink: the
    /// self tests in this codebase are run by a reflection harness that calls a fixture's Run()
    /// directly and never goes through Program.Main, so anything Main installs is not installed
    /// for them. A real source as the default would mean every suite run quietly made outbound
    /// HTTPS requests to api.github.com. Program.Main installs GitHubReleaseSource for a normal
    /// run and only for a normal run. Do not "fix" this default to the real one.
    /// </summary>
    public class OfflineReleaseSource : IReleaseSource
    {
        public ReleaseInfo TryGetLatestRelease()
        {
            return null;
        }
    }

    [DataContract]
    internal class GitHubRelease
    {
        [DataMember(Name = "tag_name")]
        public string TagName { get; set; }

        [DataMember(Name = "html_url")]
        public string HtmlUrl { get; set; }

        [DataMember(Name = "draft")]
        public bool Draft { get; set; }

        [DataMember(Name = "prerelease")]
        public bool Prerelease { get; set; }
    }

    /// <summary>
    /// The only production IReleaseSource. Deliberately small: one GET, a handful of fields, a
    /// short timeout, and a catch-everything that turns any failure into null.
    ///
    /// Modelled with DataContractJsonSerializer rather than a hand-rolled parse because
    /// "html_url" appears several times in a GitHub release payload - on the author, on the
    /// release itself - and a regex or an IndexOf would pick whichever came first. Declaring the
    /// contract means only the top-level object's fields are ever read. The serializer is part of
    /// the .NET Framework, so this keeps the "two files, nothing to fetch from NuGet" property
    /// the releases have always advertised.
    /// </summary>
    public class GitHubReleaseSource : IReleaseSource
    {
        public const string LatestReleaseUrl =
            "https://api.github.com/repos/SwatX18/vibranceGUI/releases/latest";

        // GitHub rejects an API request with no User-Agent outright (403), which is the single
        // most common way a first attempt at this fails.
        private const string UserAgent = "vibranceGUI-update-check";

        private const int TimeoutMilliseconds = 8000;

        private readonly string _url;

        public GitHubReleaseSource()
            : this(LatestReleaseUrl)
        {
        }

        public GitHubReleaseSource(string url)
        {
            _url = url;
        }

        public ReleaseInfo TryGetLatestRelease()
        {
            try
            {
                // .NET Framework 4.8 negotiates from the system default, which on a supported
                // Windows includes TLS 1.2 - but an older policy or a locked-down machine can
                // still leave it off, and GitHub requires 1.2 or better. Adding the flag is
                // harmless where it is already on. OR rather than assignment so this never
                // narrows what the rest of the process (or a future caller) already allows.
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(_url);
                request.UserAgent = UserAgent;
                request.Accept = "application/vnd.github+json";
                request.Method = "GET";
                request.Timeout = TimeoutMilliseconds;
                request.ReadWriteTimeout = TimeoutMilliseconds;

                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                {
                    if (response.StatusCode != HttpStatusCode.OK)
                    {
                        return null;
                    }

                    using (Stream stream = response.GetResponseStream())
                    {
                        if (stream == null)
                        {
                            return null;
                        }
                        return ReadRelease(stream);
                    }
                }
            }
            catch (Exception)
            {
                // Every failure is the same failure as far as the user is concerned: no network,
                // DNS, a proxy demanding auth, a 403 from the unauthenticated rate limit (60 an
                // hour per IP - fine for one user, reachable from behind a corporate NAT), a TLS
                // refusal, or malformed JSON. All of them mean "say nothing this time".
                return null;
            }
        }

        internal static ReleaseInfo ReadRelease(Stream stream)
        {
            DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(GitHubRelease));
            GitHubRelease release = serializer.ReadObject(stream) as GitHubRelease;
            if (release == null || release.Draft || release.Prerelease)
            {
                return null;
            }

            Version version;
            if (!UpdateCheckPolicy.TryParseVersion(release.TagName, out version))
            {
                return null;
            }

            return new ReleaseInfo(version, release.TagName, release.HtmlUrl);
        }
    }

    /// <summary>
    /// Static injection point, mirroring LogSink.Current - a single swappable static with a
    /// public setter so a check can save what was there, install a fake, and put the original
    /// back rather than assuming the default.
    /// </summary>
    public static class ReleaseSource
    {
        private static IReleaseSource _current = new OfflineReleaseSource();

        public static IReleaseSource Current
        {
            get { return _current; }
            set { _current = value ?? new OfflineReleaseSource(); }
        }

        public static void ResetForTests()
        {
            _current = new OfflineReleaseSource();
        }
    }

    /// <summary>
    /// Everything about the update check that can be decided without a network, a clock or a
    /// settings file - which is all of the parts worth testing. Every input is a parameter.
    /// </summary>
    public static class UpdateCheckPolicy
    {
        /// <summary>
        /// How long to wait between checks. A tray app that autostarts at login and gets
        /// restarted a dozen times while someone tests games must not ask GitHub a dozen times:
        /// the unauthenticated API allows 60 requests an hour per IP, which is generous for one
        /// user and not generous at all for an office behind one NAT.
        /// </summary>
        public static readonly TimeSpan MinimumInterval = TimeSpan.FromHours(12);

        /// <summary>
        /// The first release whose x64 build could detect an NVIDIA GPU at all. An x64 build older
        /// than this is not merely out of date, it is comprehensively broken on any machine where
        /// NVIDIA is the only vendor, and the notification says so rather than being coy.
        /// </summary>
        public static readonly Version FirstWorkingX64Version = new Version(2, 10, 1, 0);

        /// <summary>
        /// Parses "v2.10.2", "2.10.2", "2.10" or "2.10.2.0" into a Version, and rejects anything
        /// else. The leading "v" is how the tags in this repository are spelled; Version.TryParse
        /// does not strip it.
        /// </summary>
        public static bool TryParseVersion(string text, out Version version)
        {
            version = null;
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            string trimmed = text.Trim();
            if (trimmed.Length > 1 && (trimmed[0] == 'v' || trimmed[0] == 'V'))
            {
                trimmed = trimmed.Substring(1);
            }

            Version parsed;
            if (!Version.TryParse(trimmed, out parsed))
            {
                return false;
            }

            version = Normalize(parsed);
            return true;
        }

        /// <summary>
        /// Fills in the components Version leaves at -1 when they were absent from the text.
        ///
        /// This is not cosmetic. Version's comparison treats an unspecified component as less than
        /// a specified zero, so Version.Parse("2.10") &lt; Version.Parse("2.10.0") is true - which
        /// would have this app announce an "update" from 2.10 to 2.10.0, the same release written
        /// two ways. Normalising both sides removes the whole class.
        /// </summary>
        private static Version Normalize(Version version)
        {
            return new Version(
                version.Major < 0 ? 0 : version.Major,
                version.Minor < 0 ? 0 : version.Minor,
                version.Build < 0 ? 0 : version.Build,
                version.Revision < 0 ? 0 : version.Revision);
        }

        /// <summary>
        /// Compares two already-parsed versions. Kept separate from the parse so a caller cannot
        /// accidentally compare the raw strings - "2.9.0" sorts after "2.10.2" lexically, and a
        /// string comparison here would have stayed silently correct until the first minor bump
        /// and then stopped announcing anything.
        /// </summary>
        public static UpdateAvailability Compare(Version current, Version latest)
        {
            if (current == null || latest == null)
            {
                return UpdateAvailability.Unknown;
            }
            return latest > current ? UpdateAvailability.UpdateAvailable : UpdateAvailability.UpToDate;
        }

        /// <summary>
        /// Whether a check is due. Disabled always wins; a never-checked install is always due;
        /// and a last-checked stamp in the future - a clock corrected backwards, a settings file
        /// copied from another machine - counts as due rather than locking the check out until
        /// the clock catches up.
        /// </summary>
        public static bool ShouldCheckNow(bool enabled, DateTime lastCheckUtc, DateTime nowUtc)
        {
            if (!enabled)
            {
                return false;
            }
            if (lastCheckUtc == default(DateTime))
            {
                return true;
            }
            if (nowUtc < lastCheckUtc)
            {
                return true;
            }
            return nowUtc - lastCheckUtc >= MinimumInterval;
        }

        /// <summary>
        /// The balloon text. Two shapes: an ordinary "there is a newer version", and a blunter one
        /// for an x64 build from before v2.10.1, which could not detect an NVIDIA GPU at all. A
        /// user on such a build who has an AMD card, or both vendors' drivers, is running
        /// something that works - so the wording says what was broken and for whom rather than
        /// asserting that their copy is broken.
        /// </summary>
        public static string BuildNotificationText(Version current, Version latest, bool is64BitProcess)
        {
            StringBuilder text = new StringBuilder();
            text.AppendFormat("vibranceGUI {0} is available - you are on {1}.",
                FormatVersion(latest), FormatVersion(current));

            if (is64BitProcess && current < FirstWorkingX64Version)
            {
                text.AppendLine();
                text.Append("The x64 build before 2.10.1 could not detect NVIDIA GPUs. ");
                text.Append("If this one works for you it is because you are on AMD, or have both ");
                text.Append("drivers installed - updating is still worth it.");
            }

            text.AppendLine();
            text.Append("Click to open the download page.");
            return text.ToString();
        }

        /// <summary>
        /// Trims the trailing zero components a four-part Version always prints, so the balloon
        /// says "2.10.2" and not "2.10.2.0" - the releases, the tags and the title bar all spell
        /// it the short way.
        /// </summary>
        public static string FormatVersion(Version version)
        {
            if (version == null)
            {
                return string.Empty;
            }
            // Three components always, four only when a revision is actually set. Trimming a
            // zero Build as well would print v2.10.0 as "2.10", which is not how the tag, the
            // release page or the title bar spell it - and the balloon comparing "2.10" against
            // "2.10.2" reads like a bigger jump than it is. Caught by the fixture, not by eye.
            return version.Revision > 0 ? version.ToString(4) : version.ToString(3);
        }
    }
}
