import javax.inject.Inject

plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
    id("org.jetbrains.kotlin.plugin.compose")
}

/** Copies the repository's effects folder (shared with web and iOS) to assets/effects. */
abstract class CopyEffectsTask @Inject constructor(private val files: FileSystemOperations) : DefaultTask() {
    @get:InputDirectory
    @get:PathSensitive(PathSensitivity.RELATIVE)
    abstract val source: DirectoryProperty

    @get:OutputDirectory
    abstract val outputDir: DirectoryProperty

    @TaskAction
    fun copy() {
        files.sync {
            from(source)
            into(outputDir.dir("effects"))
            exclude("**/.*")
        }
    }
}

val copyEffects = tasks.register<CopyEffectsTask>("copyEffects") {
    source.set(rootProject.layout.projectDirectory.dir("../effects"))
}

androidComponents {
    onVariants { variant ->
        variant.sources.assets?.addGeneratedSourceDirectory(copyEffects, CopyEffectsTask::outputDir)
    }
}

android {
    namespace = "com.example.myapplication"
    compileSdk = 36

    defaultConfig {
        applicationId = "com.example.myapplication"
        minSdk = 24
        targetSdk = 34
        versionCode = 1
        versionName = "1.0"

        testInstrumentationRunner = "androidx.test.runner.AndroidJUnitRunner"
    }

    buildTypes {
        release {
            isMinifyEnabled = false
            proguardFiles(
                getDefaultProguardFile("proguard-android-optimize.txt"),
                "proguard-rules.pro"
            )
        }
    }
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_1_8
        targetCompatibility = JavaVersion.VERSION_1_8
    }
    kotlin {
        compilerOptions {
            jvmTarget.set(org.jetbrains.kotlin.gradle.dsl.JvmTarget.JVM_1_8)
        }
    }
    buildFeatures {
        compose = true
    }
    // MediaPipe memory-maps its models, so they must be stored uncompressed.
    androidResources {
        noCompress += "task"
    }
}

dependencies {
    // Newer BOMs (Compose 1.12+) need AGP 9.1 and compileSdk 37.
    val composeBom = platform("androidx.compose:compose-bom:2026.06.01")
    implementation(composeBom)
    // Material 3 Expressive components (floating toolbar, loading indicator, shape morphing) are alpha-only.
    implementation("androidx.compose.material3:material3:1.5.0-alpha18")
    implementation("androidx.compose.ui:ui")
    implementation("androidx.compose.ui:ui-tooling-preview")
    debugImplementation("androidx.compose.ui:ui-tooling")

    implementation("androidx.core:core-ktx:1.17.0")
    implementation("androidx.activity:activity-compose:1.13.0")
    implementation("androidx.lifecycle:lifecycle-viewmodel-compose:2.10.0")
    implementation("androidx.lifecycle:lifecycle-runtime-compose:2.10.0")

    testImplementation("junit:junit:4.13.2")
    androidTestImplementation("androidx.test.ext:junit:1.3.0")
    androidTestImplementation("androidx.test.espresso:espresso-core:3.7.0")

    // Signaling WebSocket of both call modes
    implementation("com.squareup.okhttp3:okhttp:4.12.0")

    implementation("io.github.webrtc-sdk:android:150.7871.01")
//    implementation("io.getstream:stream-webrtc-android:1.1.3") // same WebRTC API, no code change required
    implementation("com.google.mediapipe:tasks-vision:1.0.0")
}
