<img src="icon.png" width="64" align="left" style="margin-right: 20px; border-radius: 10px;">

# Smart Personal Backup Engine

A high-performance, automated, incremental backup utility for Windows designed to cleanly mirror critical system and profile folders into an isolated, high-integrity virtual disk container.

## Features

- **Automated VHDX Containerization**: Dynamically provisions and configures an expandable 100 GB VHDX storage block on-demand.
- **ReFS & Dev Drive Performance**: Formats backing containers natively using Microsoft's Resilient File System (ReFS) with developer volume optimizations (`-DevDrive` and volume trust configurations) to maximize throughput via block-cloning capabilities.
- **Dynamic Profile Mapping**: Dynamically expanding user environments at runtime (resolves down directly to active user directories such as `C:\Users\Michael`) without hardcoding persistent personal pathways into source files.
- **Intelligent Real-Time Exclusions**: Skips gigabytes of temporary junk, logs, web browser caches, diagnostic traces, system page/swap blocks, as well as developer noise like `node_modules`, `bin/`, and `obj/` trees.
- **Background Execution & Tray Support**: Built with Windows power-throttling bypasses (`SetThreadExecutionState`) to prevent sleep states during backup operations, minimizing smoothly into the system tray when processing.
- **Task Scheduler Automation**: Built-in support to register/unregister high-integrity `WakeToRun` daily backup tasks to execute automatically at 12:00 AM Midnight.

## Tech Stack

- **Framework**: .NET 8.0 Windows (WPF)
- **Language**: C# 12
- **Automation Interop**: Windows PowerShell / `diskpart.exe` Volume Management Cmdlets

## Core Scope Scanned

- Local Active User Profile (`%USERPROFILE%`)
- Global Shared Program Data (`C:\ProgramData`)

## Target Backup Location

- Configured to output securely to: `F:\Backup\Home-PC\BackupDev.vhdx`
