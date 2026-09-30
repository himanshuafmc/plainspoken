plugins {
    // Android Gradle Plugin 9 compiles Kotlin itself ("built-in Kotlin"), so there is no separate
    // org.jetbrains.kotlin.android plugin; the Kotlin version comes with AGP.
    id("com.android.application") version "9.4.1" apply false
}
