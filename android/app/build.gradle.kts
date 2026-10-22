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

        // The offline fallback's whisper.cpp engine (src/main/cpp). 64-bit ARM is what phones run; 32-bit ARM and
        // x86_64 (emulators) are built too so the APK still installs there.
        ndk {
            abiFilters += listOf("arm64-v8a", "armeabi-v7a", "x86_64")
        }
        externalNativeBuild {
            cmake {
                // Several native libraries share one C++ runtime.
                arguments += listOf("-DANDROID_STL=c++_shared")
            }
        }
    }

    externalNativeBuild {
        cmake {
            path = file("src/main/cpp/CMakeLists.txt")
            version = "3.22.1"
        }
    }

    // Two editions of the same app (same package, same key, so either replaces the other with settings kept):
    // "standard" and "floatingMic", which adds the floating mic's accessibility service (src/floatingMic/AndroidManifest.xml).
    // They are separate because Play Protect in India blocks installing apps that declare an accessibility service
    // from a browser or file manager; the floating-mic edition needs scanning off or a USB install (see README).
    flavorDimensions += "edition"
    productFlavors {
        create("standard") {
            dimension = "edition"
        }
        create("floatingMic") {
            dimension = "edition"
        }
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
        // Native libraries are unpacked on install, so whisper.cpp can find its CPU backends in the app's lib folder.
        jniLibs {
            useLegacyPackaging = true
        }
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
    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-android:1.11.0")
}
