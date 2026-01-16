package com.example.myapplication

import android.content.Context
import android.os.Build
import android.view.WindowManager

object Utils {
    
    data class ScreenDimensions(
        @JvmField val screenWidth: Int,
        @JvmField val screenHeight: Int
    )

    @JvmStatic
    fun getScreenDimentions(context: Context): ScreenDimensions {
        val windowManager = context.getSystemService(Context.WINDOW_SERVICE) as WindowManager
        
        return if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
            val windowMetrics = windowManager.currentWindowMetrics
            val bounds = windowMetrics.bounds
            ScreenDimensions(bounds.width(), bounds.height())
        } else {
            @Suppress("DEPRECATION")
            val displayMetrics = android.util.DisplayMetrics()
            @Suppress("DEPRECATION")
            windowManager.defaultDisplay.getRealMetrics(displayMetrics)
            ScreenDimensions(displayMetrics.widthPixels, displayMetrics.heightPixels)
        }
    }

    @JvmStatic
    fun getFps(context: Context): Int {
        val refreshRate = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
            context.display.refreshRate
        } else {
            @Suppress("DEPRECATION")
            val windowManager = context.getSystemService(Context.WINDOW_SERVICE) as WindowManager
            @Suppress("DEPRECATION")
            windowManager.defaultDisplay.refreshRate
        }

        return when {
            refreshRate >= 90 -> 60  // Use 60 FPS for high refresh rate displays
            refreshRate >= 60 -> 30  // Use 30 FPS for standard displays
            else -> 15              // Use 15 FPS for lower refresh rate displays
        }
    }
}
