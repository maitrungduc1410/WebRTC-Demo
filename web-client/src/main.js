import Vue from 'vue'
import App from './App.vue'
import io from 'socket.io-client'

Vue.config.productionTip = false

new Vue({
  render: h => h(App),
  data: {
    BASE_URL: 'http://localhost:4000',
    socket: {}
  },
  created() {
    this.socket = io(this.BASE_URL)
    this.socket.on('connect', () => {
      console.log('Socket connected')
    })
  }
}).$mount('#app')
