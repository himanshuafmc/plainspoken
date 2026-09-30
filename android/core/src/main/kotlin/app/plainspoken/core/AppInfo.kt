package app.plainspoken.core

/** App identity in one place. File names and user-visible strings are derived from [NAME]. */
object AppInfo {
    const val NAME = "Plainspoken"

    /** Set by the app at start-up from its package version, e.g. "0.2.0". */
    @Volatile
    var version: String = "0.0.0"
}
