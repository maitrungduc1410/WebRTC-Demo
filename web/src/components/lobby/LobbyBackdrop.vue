<template>
  <!-- Slowly drifting colour fields in the app's indigo / violet palette, like the mobile lobbies. -->
  <div class="pointer-events-none absolute inset-0 overflow-hidden" aria-hidden="true">
    <div class="absolute -top-1/4 -left-1/4 size-[70vmax] animate-drift-a rounded-full bg-indigo-300/50 blur-3xl dark:bg-indigo-700/35" />
    <div class="absolute -right-1/4 -bottom-1/3 size-[65vmax] animate-drift-b rounded-full bg-violet-300/45 blur-3xl dark:bg-violet-800/35" />
    <div class="absolute top-1/4 left-1/3 size-[45vmax] animate-drift-c rounded-full bg-sky-200/50 blur-3xl dark:bg-blue-800/25" />
    <div class="absolute inset-0 bg-[radial-gradient(transparent_0,var(--background)_85%)] opacity-60" />
  </div>
</template>
