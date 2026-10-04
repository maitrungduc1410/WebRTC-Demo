package com.example.myapplication

import android.content.Intent
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import com.example.myapplication.call.BaseCallViewModel
import com.example.myapplication.settings.SfuServer
import com.example.myapplication.settings.SignalingServer
import com.example.myapplication.ui.lobby.LobbyScreen
import com.example.myapplication.ui.lobby.ServerSettings
import com.example.myapplication.ui.theme.AppTheme

class MainActivity : ComponentActivity() {

    override fun onCreate(savedInstanceState: Bundle?) {
        enableEdgeToEdge()
        super.onCreate(savedInstanceState)

        val context = this
        setContent {
            AppTheme {
                var serverAddress by remember { mutableStateOf(SignalingServer.load(context)) }
                var sfuAddress by remember { mutableStateOf(SfuServer.load(context)) }
                LobbyScreen(
                    serverAddress = serverAddress,
                    defaultServerAddress = SignalingServer.defaultAddress(context),
                    onServerAddressChange = { address ->
                        SignalingServer.save(context, address)
                        serverAddress = address
                    },
                    sfu = ServerSettings(
                        address = sfuAddress,
                        defaultAddress = SfuServer.defaultAddress(context),
                        onChange = { address ->
                            SfuServer.save(context, address)
                            sfuAddress = address
                        }
                    )
                ) { roomId, e2ee, group ->
                    val intent = Intent(context, CallActivity::class.java)
                        .putExtra(BaseCallViewModel.EXTRA_ROOM_ID, roomId)
                        .putExtra(BaseCallViewModel.EXTRA_E2EE, e2ee)
                        .putExtra(BaseCallViewModel.EXTRA_SERVER_ADDRESS, serverAddress)
                    if (group) {
                        intent.putExtra(BaseCallViewModel.EXTRA_GROUP, true)
                            .putExtra(BaseCallViewModel.EXTRA_SFU_ADDRESS, sfuAddress)
                    }
                    startActivity(intent)
                }
            }
        }
    }
}
