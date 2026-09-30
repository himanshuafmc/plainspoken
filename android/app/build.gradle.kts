import org.jetbrains.kotlin.gradle.dsl.JvmTarget

plugins {
    id("com.android.application")
}

// Both apps share one version number: the VERSION file at the repository root.
val appVersion = rootDir.resolve("../VERSION").readText().trim()
val versionParts = appVersion.split(".").map { it.toInt() }

// Release signing comes from the environment (GitHub Actions secrets). Without it, the release APK is signed
// with the debug key so it can still be installed for testing.
val keystorePath: String? = System.getenv("PLAINSPOKEN_KEYSTORE")?.takeIf { it.isNotBlank() }

android {
    namespace = "app.plainspoken"
    // OkHttp 5 needs compileSdk 37 or later. targetSdk stays 35, so the app's behaviour on phones is unchanged.
    compileSdk = 37

    defaultConfig {
        applicationId = "app.plainspoken"
        minSdk = 26
        targetSdk = 35
        versionCode = versionParts[0] * 10000 + versionParts[1] * 100 + versionParts[2]
        versionName = appVersion
    }

    signingConfigs {
        if (keystorePath != null) {
            create("release") {
                storeFile = file(keystorePath)
                storePassword = System.getenv("PLAINSPOKEN_KEYSTORE_PASSWORD")
                keyAlias = System.getenv("PLAINSPOKEN_KEY_ALIAS")
                keyPassword = System.getenv("PLAINSPOKEN_KEY_PASSWORD")
            }
        }
    }

    buildTypes {
        release {
            // Kept off for now: the app is small and every class stays exactly as tested.
            isMinifyEnabled = false
            signingConfig = if (keystorePath != null) signingConfigs.getByName("release") else signingConfigs.getByName("debug")
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    buildFeatures {
        buildConfig = false
    }

    packaging {
        resources {
            excludes += setOf("META-INF/*.kotlin_module", "META-INF/versions/**", "DebugProbesKt.bin", "kotlin/**.kotlin_builtins")
        }
    }

    lint {
        abortOnError = true
        warningsAsErrors = false
        checkReleaseBuilds = true
    }
}

kotlin {
    compilerOptions {
        jvmTarget.set(JvmTarget.JVM_17)
    }
}

dependencies {
    implementation("app.plainspoken:core")
    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-android:1.10.2")
}
