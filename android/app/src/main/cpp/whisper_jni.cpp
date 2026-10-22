// JNI bridge between app.plainspoken.platform.WhisperNative and whisper.cpp: load a model, transcribe 16 kHz mono
// samples, free the model. Text comes back as UTF-8 bytes (JNI's own string encoding can't hold every character).

#include <jni.h>

#include <string>
#include <vector>

#include "ggml-backend.h"
#include "whisper.h"

namespace {

std::string ToString(JNIEnv* env, jstring value) {
    if (value == nullptr) return {};
    const char* chars = env->GetStringUTFChars(value, nullptr);
    std::string out = chars != nullptr ? chars : "";
    if (chars != nullptr) env->ReleaseStringUTFChars(value, chars);
    return out;
}

}  // namespace

extern "C" {

JNIEXPORT void JNICALL Java_app_plainspoken_platform_WhisperNative_loadBackends(JNIEnv* env, jobject, jstring dir) {
    ggml_backend_load_all_from_path(ToString(env, dir).c_str());
}

JNIEXPORT jlong JNICALL Java_app_plainspoken_platform_WhisperNative_open(JNIEnv* env, jobject, jstring path) {
    whisper_context_params params = whisper_context_default_params();
    params.use_gpu = false;
    whisper_context* context = whisper_init_from_file_with_params(ToString(env, path).c_str(), params);
    return reinterpret_cast<jlong>(context);
}

JNIEXPORT jbyteArray JNICALL Java_app_plainspoken_platform_WhisperNative_transcribe(
        JNIEnv* env, jobject, jlong handle, jfloatArray samples, jstring language, jstring prompt, jint threads) {
    auto* context = reinterpret_cast<whisper_context*>(handle);
    if (context == nullptr || samples == nullptr) return nullptr;

    const jsize count = env->GetArrayLength(samples);
    std::vector<float> pcm(static_cast<size_t>(count));
    env->GetFloatArrayRegion(samples, 0, count, pcm.data());
    const std::string lang = ToString(env, language);
    const std::string initial = ToString(env, prompt);

    whisper_full_params params = whisper_full_default_params(WHISPER_SAMPLING_GREEDY);
    params.n_threads = threads > 0 ? threads : 1;
    params.language = lang.empty() ? "auto" : lang.c_str();
    params.initial_prompt = initial.empty() ? nullptr : initial.c_str();
    params.translate = false;
    params.no_timestamps = true;
    params.print_progress = false;
    params.print_realtime = false;
    params.print_special = false;
    params.print_timestamps = false;
    params.suppress_blank = true;
    if (whisper_full(context, params, pcm.data(), static_cast<int>(pcm.size())) != 0) return nullptr;

    std::string text;
    const int segments = whisper_full_n_segments(context);
    for (int i = 0; i < segments; ++i) {
        text += whisper_full_get_segment_text(context, i);
        text += ' ';
    }

    jbyteArray out = env->NewByteArray(static_cast<jsize>(text.size()));
    if (out != nullptr) {
        env->SetByteArrayRegion(out, 0, static_cast<jsize>(text.size()), reinterpret_cast<const jbyte*>(text.data()));
    }

    return out;
}

JNIEXPORT void JNICALL Java_app_plainspoken_platform_WhisperNative_close(JNIEnv*, jobject, jlong handle) {
    whisper_free(reinterpret_cast<whisper_context*>(handle));
}

}  // extern "C"
