package app.plainspoken

import android.app.Application
import android.content.Context
import android.os.Build
import app.plainspoken.core.AppInfo

class PlainspokenApp : Application() {
    lateinit var services: Services
        private set

    override fun onCreate() {
        super.onCreate()
        AppInfo.version = versionName()
        services = Services(this)
        val previous = Thread.getDefaultUncaughtExceptionHandler()
        Thread.setDefaultUncaughtExceptionHandler { thread, e ->
            services.log.error("crash on ${thread.name}", e)
            previous?.uncaughtException(thread, e)
        }

        services.log.info("${AppInfo.NAME} ${AppInfo.version} started on Android ${Build.VERSION.RELEASE} (API ${Build.VERSION.SDK_INT})")
    }

    private fun versionName(): String = try {
        @Suppress("DEPRECATION")
        packageManager.getPackageInfo(packageName, 0).versionName ?: "0.0.0"
    } catch (_: Exception) {
        "0.0.0"
    }

    companion object {
        fun services(context: Context): Services = (context.applicationContext as PlainspokenApp).services
    }
}
