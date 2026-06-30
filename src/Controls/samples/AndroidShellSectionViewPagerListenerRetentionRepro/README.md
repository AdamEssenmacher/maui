# Android ShellSectionRenderer ViewPager Listener Retention Repro

This repro isolates the Android ShellSectionRenderer native listener root. The current run registers a real `ShellSectionRenderer` as the `TabLayoutMediator.ITabConfigurationStrategy` and registers MAUI's private `ViewPagerPageChanged` callback through the same `PlatformInterop.createShellViewPager` helper used by Shell. Retained native `ViewPager2`/`TabLayout` peers then keep the disposed renderer and its Shell/MauiContext payload graph alive.

The control run keeps the same native peers alive, but uses stateless callback/strategy objects so the discarded renderer and payload graph can collect.
