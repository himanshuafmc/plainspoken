import org.jetbrains.kotlin.gradle.dsl.JvmTarget

// Portable Plainspoken logic in plain Kotlin: settings, Gemini engines, live streaming, text joining,
// storage and the dictation state machine. A port of windows/src/Plainspoken.Core that follows
// docs/SPEC.md; both are tested against the files in shared/test-fixtures.
plugins {
    kotlin("jvm") version "2.4.21"
    kotlin("plugin.serialization") version "2.4.21"
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
    api("org.jetbrains.kotlinx:kotlinx-coroutines-core:1.12.0")
    api("org.jetbrains.kotlinx:kotlinx-serialization-json:1.12.0")
    api("com.squareup.okhttp3:okhttp:5.5.1")

    testImplementation(kotlin("test"))
    testImplementation("org.jetbrains.kotlinx:kotlinx-coroutines-test:1.12.0")
    testImplementation("org.junit.jupiter:junit-jupiter:6.1.3")
    testRuntimeOnly("org.junit.platform:junit-platform-launcher:6.1.3")
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

// Runs shared/test-fixtures/cleanup-cases.json through the real clean-up service and scores the results.
// Needs GEMINI_API_KEY (or a proxy that adds the key). Never part of the normal build or CI.
//   ./gradlew -p core cleanupEval [-Pmodel=gemini-3.5-flash] [-Pcases=hi-]
val eval: SourceSet by sourceSets.creating {
    compileClasspath += sourceSets.main.get().output
    runtimeClasspath += sourceSets.main.get().output
}
configurations[eval.implementationConfigurationName].extendsFrom(configurations.api.get(), configurations.implementation.get())

tasks.register<JavaExec>("cleanupEval") {
    group = "verification"
    description = "Scores the clean-up step against the shared test set using the real API."
    classpath = eval.runtimeClasspath
    mainClass.set("app.plainspoken.core.CleanupEvalKt")
    systemProperty("plainspoken.fixtures", rootDir.resolve("../../shared/test-fixtures").absolutePath)
    jvmArgs("-Dstdout.encoding=UTF-8", "-Dfile.encoding=UTF-8")
    args = listOf(
        (project.findProperty("model") ?: "gemini-3.5-flash").toString(),
        (project.findProperty("cases") ?: "").toString(),
        (project.findProperty("delayMs") ?: "6500").toString(),
    )
}
