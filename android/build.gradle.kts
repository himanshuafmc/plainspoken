// Android Gradle Plugin 9 compiles Kotlin itself ("built-in Kotlin"), so there is no separate
// org.jetbrains.kotlin.android plugin. AGP brings an older Kotlin compiler, but the portable `core` build is
// compiled with a newer Kotlin; the app compiler must be at least as new to read core's classes. Keep this
// version equal to the Kotlin version in core/build.gradle.kts.
buildscript {
    repositories {
        google()
        mavenCentral()
    }
    dependencies {
        classpath("org.jetbrains.kotlin:kotlin-gradle-plugin:2.4.21")
    }
}

plugins {
    id("com.android.application") version "9.4.1" apply false
}
