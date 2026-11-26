# What

Jailbreak tool for Control4. Lets you add, rename and move rooms, drivers and devices in Composer Pro without being a dealer.

Check the [latest release](https://github.com/black-ops-drivers/Control4.Jailbreak/releases/latest) for the Composer and controller OS versions it supports.

# Why

Home automation is more fun when you can play with it. Write your own drivers, make your house do cool stuff. By default you can't touch your own project without going through an authorized dealer.

This tool is open source, so you can see exactly what it changes. And if you change your mind, it can undo itself.

# How

1. Head over to the [releases page](https://github.com/black-ops-drivers/Control4.Jailbreak/releases/latest) and download the zip (not the source). Unzip and run C4Jailbreak.exe.
2. Install the Composer Pro version listed in the release notes.
3. Enter your controller's IP address and click **Jailbreak**.
4. If the status bar says "Management pack not installed", click **Install Pack**.
5. Open Composer and connect to your project.

The release notes have the full steps for each version, including how to upgrade your controller.

## Check for the c4diagd kit

In November 2026 a malicious "c4diagd" kit was staged on many Control4 controllers. To check yours, click the arrow next to **Jailbreak** and pick **Check for c4diagd (read-only)**, then click the button. It needs root SSH (run **SSH Access** first if you haven't). The check copies a script to `/tmp` on the controller, runs it, deletes it, and saves a report in the `c4diag reports` folder next to the app. It changes nothing on the controller.

Results: **NO INDICATORS FOUND**, **POSSIBLE PAST INFECTION**, **PREVIOUSLY INFECTED, NOW CLEAN**, **INFECTED (staged)** or **ARMED**. If it says ARMED, do not reboot or reset the controller; contact Control4.

The script is also in [`Tools/c4diag`](Tools/c4diag) if you want to run it by hand. It comes from [jwheeler188/c4diag-check](https://github.com/jwheeler188/c4diag-check), which has the latest version, a step-by-step guide and launchers for Windows and Mac.

## Undo It

Click the arrow next to **Jailbreak** and pick **Revert**. It undoes the jailbreak on your PC and on the controller.

## Enjoy

You're all done

# Other stuff

Everything is at your own risk, obviously. If this tool leads to your house burning down you can't blame us.

Originally written by Garry Newman. Now maintained by Black Ops Drivers.

For help, the Control4 DIY community is over at https://www.reddit.com/r/C4diy/.
