package com.example.myapplication.ui.call

import org.junit.Assert.assertEquals
import org.junit.Test

class GroupGridTest {

    @Test
    fun portraitStacksTwoThenUsesTwoColumns() {
        assertEquals(listOf(1, 1, 2, 2, 2, 2, 3), (1..7).map { gridColumns(it, landscape = false) })
    }

    @Test
    fun landscapePutsUpToThreeSideBySide() {
        assertEquals(listOf(1, 2, 3, 2, 3, 3, 4), (1..7).map { gridColumns(it, landscape = true) })
    }
}
