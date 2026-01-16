package com.example.myapplication

import android.app.Activity
import android.content.Context
import android.content.pm.PackageManager
import androidx.core.app.ActivityCompat

class PermissionChecker {
    
    private val REQUEST_MULTIPLE_PERMISSION = 100
    private var callbackMultiple: VerifyPermissionsCallback? = null

    fun verifyPermissions(
        activity: Activity,
        permissions: Array<String>,
        callback: VerifyPermissionsCallback?
    ) {
        val denyPermissions = getDenyPermissions(activity, permissions)
        if (denyPermissions.isNotEmpty()) {
            ActivityCompat.requestPermissions(activity, denyPermissions, REQUEST_MULTIPLE_PERMISSION)
            this.callbackMultiple = callback
        } else {
            callback?.onPermissionAllGranted()
        }
    }

    private fun getDenyPermissions(context: Context, permissions: Array<String>): Array<String> {
        val denyPermissions = mutableListOf<String>()
        for (permission in permissions) {
            if (ActivityCompat.checkSelfPermission(context, permission) != PackageManager.PERMISSION_GRANTED) {
                denyPermissions.add(permission)
            }
        }
        return denyPermissions.toTypedArray()
    }

    fun onRequestPermissionsResult(
        requestCode: Int,
        permissions: Array<String>,
        grantResults: IntArray
    ) {
        when (requestCode) {
            REQUEST_MULTIPLE_PERMISSION -> {
                if (grantResults.isNotEmpty() && callbackMultiple != null) {
                    val denyPermissions = mutableListOf<String>()
                    permissions.forEachIndexed { index, permission ->
                        if (grantResults[index] == PackageManager.PERMISSION_DENIED) {
                            denyPermissions.add(permission)
                        }
                    }
                    if (denyPermissions.isEmpty()) {
                        callbackMultiple?.onPermissionAllGranted()
                    } else {
                        callbackMultiple?.onPermissionDeny(denyPermissions.toTypedArray())
                    }
                }
            }
        }
    }

    interface VerifyPermissionsCallback {
        fun onPermissionAllGranted()
        fun onPermissionDeny(permissions: Array<String>)
    }

    companion object {
        fun hasPermissions(context: Context, permissions: Array<String>): Boolean {
            for (permission in permissions) {
                if (ActivityCompat.checkSelfPermission(context, permission) != PackageManager.PERMISSION_GRANTED) {
                    return false
                }
            }
            return true
        }
    }
}
