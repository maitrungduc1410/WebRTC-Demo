package com.example.myapplication

import android.content.Context
import android.hardware.display.DisplayManager
import android.util.DisplayMetrics
import android.view.Display

object Utils {
    
    data class ScreenDimensions(
        @JvmField val screenWidth: Int,
        @JvmField val screenHeight: Int
    )

    // DisplayManager works with the application context; WindowManager and Context.display need a visual one.
    private fun defaultDisplay(context: Context): Display =
        (context.getSystemService(Context.DISPLAY_SERVICE) as DisplayManager).getDisplay(Display.DEFAULT_DISPLAY)

    @JvmStatic
    fun getScreenDimentions(context: Context): ScreenDimensions {
        val displayMetrics = DisplayMetrics()
        @Suppress("DEPRECATION")
        defaultDisplay(context).getRealMetrics(displayMetrics)
        return ScreenDimensions(displayMetrics.widthPixels, displayMetrics.heightPixels)
    }

    @JvmStatic
    fun getFps(context: Context): Int {
        val refreshRate = defaultDisplay(context).refreshRate

        return when {
            refreshRate >= 90 -> 60  // Use 60 FPS for high refresh rate displays
            refreshRate >= 60 -> 30  // Use 30 FPS for standard displays
            else -> 15              // Use 15 FPS for lower refresh rate displays
        }
    }
}
