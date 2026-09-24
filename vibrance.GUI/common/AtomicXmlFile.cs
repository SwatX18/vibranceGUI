using System;
using System.IO;
using System.Xml;
using System.Xml.Serialization;

namespace vibrance.GUI.common
{
    /// <summary>
    /// The atomic temp-then-File.Replace write (with a delete-then-File.Move fallback for the
    /// first write a given install ever makes, when there is nothing yet to replace) and the
    /// parse-or-discard read - lifted out of RealVibranceRestoreStore, which owned this exact
    /// logic on its own before ResolutionRestoreStore (D4's resolution half) needed the identical
    /// contract for a second, separate file (resolutionRestore.xml - see ResolutionRestoreStore's
    /// own header for why it is a second file rather than a second section of the same one). The
    /// subtlety here - the narrow window a kill can still leave torn, and why that is safe anyway -
    /// is worth having exist exactly once, not copied a second time with a chance to drift.
    ///
    /// Generic over T so any XmlSerializer-shaped record can round-trip through it; T is
    /// constrained to "class" only (not "class, new()") because the caller, not this class, is the
    /// one that ever constructs a T - TryRead only ever hands back whatever XmlSerializer itself
    /// produced (or null), and Write only ever takes a T the caller already built.
    ///
    /// TryRead/TryDeleteQuietly never throw - a torn or unparseable file, or a delete that loses a
    /// filesystem race, must never escape into a caller's own startup or journaling path (see each
    /// method's own comment). Write DOES throw on failure - every current caller (RealVibranceRestoreStore,
    /// RealResolutionRestoreStore) wraps its own call in a try/catch and logs with its own message,
    /// naming which record failed to persist, exactly as RealVibranceRestoreStore's Write already
    /// did before this was a shared helper - so the try/catch stays at the caller, not here.
    /// </summary>
    internal static class AtomicXmlFile
    {
        /// <summary>
        /// The value persisted at filePath, or null when there is none, or when the file could not
        /// be parsed - a torn write (the process was killed mid-write) must read back exactly like
        /// "no record", never throw into a caller's startup path: the failure these features exist
        /// to recover from is abnormal termination, so termination mid-write is squarely in scope,
        /// not an edge case. A file that fails to parse is also deleted as part of this call - see
        /// the catch block below for why leaving it in place would help nobody.
        /// </summary>
        internal static T TryRead<T>(string filePath) where T : class
        {
            try
            {
                if (!File.Exists(filePath))
                {
                    return null;
                }

                using (XmlReader reader = XmlReader.Create(filePath))
                {
                    XmlSerializer serializer = new XmlSerializer(typeof(T));
                    return (T)serializer.Deserialize(reader);
                }
            }
            catch (Exception)
            {
                // Torn or unparseable - most likely a write this same feature interrupted by the
                // very abnormal exit it exists to recover from (see Write's own comment for the
                // narrow window that can still leave a torn file: the temp file itself, mid-write).
                // A bad record must never throw into startup, and it can never become readable on a
                // later run either, so there is nothing to gain by leaving it in place - discard it
                // now rather than have every future launch re-attempt and re-fail the same parse
                // forever.
                TryDeleteQuietly(filePath);
                return null;
            }
        }

        /// <summary>
        /// Replaces filePath with value, atomically. CDS_UPDATEREGISTRY-unrelated, but the same
        /// idea: File.Replace is a single filesystem transaction, so a process killed anywhere from
        /// here onward leaves either the OLD file untouched or the NEW one fully in place - never a
        /// half-written filePath. The only file that CAN be left torn by a kill is the ".tmp" one
        /// below, while Serialize itself is still running - TryRead's own try/catch (plus its
        /// delete-on-failure) is what makes that safe: File.Replace never even runs in that case, so
        /// the real file this method is responsible for is never the one left torn. File.Replace
        /// requires an existing destination; delete-then-move covers the first write ever made for a
        /// given install, when there is nothing yet to replace.
        ///
        /// Throws on failure (permissions, disk full, a concurrent AV scan holding the file) - see
        /// the class header for why the try/catch belongs at the caller, not here.
        /// </summary>
        internal static void Write<T>(string filePath, T value) where T : class
        {
            string tempPath = filePath + ".tmp";
            using (XmlWriter writer = XmlWriter.Create(tempPath))
            {
                XmlSerializer serializer = new XmlSerializer(typeof(T));
                serializer.Serialize(writer, value);
                writer.Flush();
            }

            if (File.Exists(filePath))
            {
                File.Replace(tempPath, filePath, null);
            }
            else
            {
                // File.Move alone would throw IOException if a file appeared at filePath between the
                // File.Exists check above and here (another thread's write, in principle - every
                // caller of this class belongs to a single-instance app, so not expected in
                // practice); the delete first makes this branch safe to fall into either way.
                File.Delete(filePath);
                File.Move(tempPath, filePath);
            }
        }

        /// <summary>
        /// Removes filePath, if it exists. A no-op, not an error, when there is nothing to remove.
        /// Best-effort: swallows its own failures, exactly like the delete-on-parse-failure branch
        /// in TryRead above - a leftover file here is inert, never actively harmful, to any caller
        /// in this codebase (see each store's own Delete()/TryRead() header for why a stale file can
        /// never make that caller misbehave).
        /// </summary>
        internal static void TryDeleteQuietly(string filePath)
        {
            try
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }
            }
            catch (Exception)
            {
                // Best-effort - see the summary above.
            }
        }
    }
}
