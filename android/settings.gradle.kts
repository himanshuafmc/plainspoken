// Plainspoken for Android. The portable logic lives in the separate `core` build (plain Kotlin,
// no Android APIs), so it can be built and tested on any machine; the app includes it.
pluginManagement {
    repositories {
        google()
        mavenCentral()
        gradlePluginPortal()
    }
}

dependencyResolutionManagement {
    repositoriesMode.set(RepositoriesMode.FAIL_ON_PROJECT_REPOS)
    repositories {
        google()
        mavenCentral()
    }
}

rootProject.name = "plainspoken-android"
includeBuild("core")
include(":app")
