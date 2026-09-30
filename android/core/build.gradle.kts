import org.jetbrains.kotlin.gradle.dsl.JvmTarget

// Portable Plainspoken logic in plain Kotlin: settings, Gemini engines, live streaming, text joining,
// storage and the dictation state machine. A port of windows/src/Plainspoken.Core that follows
// docs/SPEC.md; both are tested against the files in shared/test-fixtures.
plugins {
    kotlin("jvm") version "2.1.21"
    kotlin("plugin.serialization") version "2.1.21"
}

group = "app.plainspoken"
version = rootDir.resolve("../../VERSION").readText().trim()

java {
    sourceCompatibility = JavaVersion.VERSION_17
    targetCompatibility = JavaVersion.VERSION_17
}

kotlin {
    compilerOptions {
        jvmTarget.set(JvmTarget.JVM_17)
        allWarningsAsErrors.set(true)
    }
}

dependencies {
    api("org.jetbrains.kotlinx:kotlinx-coroutines-core:1.10.2")
    api("org.jetbrains.kotlinx:kotlinx-serialization-json:1.8.1")
    api("com.squareup.okhttp3:okhttp:4.12.0")

    testImplementation(kotlin("test"))
    testImplementation("org.jetbrains.kotlinx:kotlinx-coroutines-test:1.10.2")
    testImplementation("org.junit.jupiter:junit-jupiter:6.1.3")
    testRuntimeOnly("org.junit.platform:junit-platform-launcher:1.11.4")
}

tasks.test {
    useJUnitPlatform()
    systemProperty("plainspoken.fixtures", rootDir.resolve("../../shared/test-fixtures").absolutePath)
    systemProperty("plainspoken.shared", rootDir.resolve("../../shared").absolutePath)
    testLogging {
        events("failed")
        exceptionFormat = org.gradle.api.tasks.testing.logging.TestExceptionFormat.FULL
    }
}
