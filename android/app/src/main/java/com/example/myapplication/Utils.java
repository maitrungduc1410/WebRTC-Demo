package com.example.myapplication;

import android.content.Context;
import android.util.DisplayMetrics;
import android.view.Display;
import android.view.WindowManager;

public class Utils {

    public static class ScreenDimensions {
        public int screenWidth;
        public int screenHeight;

        public ScreenDimensions(int screenWidth, int screenHeight) {
            this.screenWidth = screenWidth;
            this.screenHeight = screenHeight;
        }
    }

    public static ScreenDimensions getScreenDimentions(Context context) {
        DisplayMetrics displayMetrics = new DisplayMetrics();
        WindowManager windowManager = (WindowManager) (context).getSystemService(Context.WINDOW_SERVICE);
        windowManager.getDefaultDisplay().getRealMetrics(displayMetrics);
        int screenWidth = displayMetrics.widthPixels;
        int screenHeight = displayMetrics.heightPixels;
        return new ScreenDimensions(screenWidth, screenHeight);
    }

    public static int getFps(Context context) {
        Display display = ((WindowManager) (context).getSystemService(Context.WINDOW_SERVICE)).getDefaultDisplay();
        float refreshRate = display.getRefreshRate();
        int fps;

        if (refreshRate >= 90) {
            fps = 60;  // Use 60 FPS for high refresh rate displays
        } else if (refreshRate >= 60) {
            fps = 30;  // Use 30 FPS for standard displays
        } else {
            fps = 15;  // Use 15 FPS for lower refresh rate displays
        }

        return fps;
    }
}
