#!/bin/sh
# c4diag-check.sh  -  READ-ONLY check for the c4diagd / c4hijack kit on a Control4 controller
# Analyst: James Wheeler   Rev 1.3 (EA-5 / OS 3.4.3 fixes from a community field report)   November 25, 2026
#
# Run as root over SSH:   sh c4diag-check.sh | tee /tmp/c4diag-check.txt
# This script only reads. It does not stop, delete or change anything.
# If it reports ARMED, stop and escalate to Control4. Do not reboot or kill processes.

echo "=== c4diag check rev 1.3  $(date)  host: $(hostname) ==="
FOUND=0; ARMED=0; CLEANED=0; TIMEHINT=0

say() { printf '%-48s %s\n' "$1" "$2"; }
NOW=$(date +%s)
REF_EPOCH=1794700800     # 2026-11-15 00:00 UTC (look-back start for changed files)
WIN_START=1795046400     # 2026-11-19 00:00 UTC (earliest known install)
WIN_END=1795564800       # 2026-11-25 00:00 UTC
DAYS=$(( (NOW - REF_EPOCH) / 86400 + 1 ))
mtime() { stat -c %Y "$1" 2>/dev/null || date -r "$1" +%s 2>/dev/null; }
fmt() { date -u -d "@$1" '+%Y-%m-%d %H:%M UTC' 2>/dev/null || echo "$1"; }
# Log files: some OS versions keep them under /var/log/debug/
LOGFILES=$(find /var/log -type f \( -name 'daemon.log*' -o -name 'messages*' -o -name 'syslog*' -o -name 'auth.log*' -o -name 'secure*' \) 2>/dev/null)
readlogs() { for f in $LOGFILES; do case "$f" in *.gz) zcat "$f" 2>/dev/null ;; *) cat "$f" 2>/dev/null ;; esac; done; }
# Root's home is /home/root on some OS versions
ROOTHOME=$(awk -F: '$1=="root"{print $6}' /etc/passwd 2>/dev/null); [ -z "$ROOTHOME" ] && ROOTHOME=/root

# 1. Kit files
# A unit file that is a link to /dev/null means c4diagd was MASKED (neutralised by
# an owner or a cleanup). That is evidence of past infection, not an active one.
for f in /usr/local/bin/c4diagd /usr/local/bin/c4diagd.cfg.json \
         /etc/systemd/system/c4diagd.service \
         /etc/systemd/system/multi-user.target.wants/c4diagd.service \
         /var/log/c4diagd.log /tmp/c4diag-setup; do
  if [ -L "$f" ] && [ "$(readlink "$f")" = "/dev/null" ]; then
    say "$f" "MASKED (link to /dev/null) - past infection"; CLEANED=1
  elif [ -L "$f" ] && [ ! -e "$f" ]; then
    say "$f" "broken link - leftover, past infection"; CLEANED=1
  elif [ -e "$f" ]; then
    say "$f" "PRESENT  <-- infection"; FOUND=1
  else
    say "$f" "not found"
  fi
done

if [ -d /opt/.c4diag ]; then
  say "/opt/.c4diag/" "PRESENT  <-- infection"; FOUND=1
  ls -la /opt/.c4diag/
  for flag in APPLIED ARMED; do
    if ls /opt/.c4diag/ 2>/dev/null | grep -q "$flag"; then say "  $flag flag" "PRESENT  <-- ARMED"; ARMED=1; fi
  done
else
  say "/opt/.c4diag/" "not found"
fi

# 2. Persistence on older units
if grep -q c4diagd /etc/inittab 2>/dev/null; then
  say "/etc/inittab respawn line" "PRESENT  <-- infection"; FOUND=1
  grep -n c4diagd /etc/inittab
else
  say "/etc/inittab respawn line" "not found"
fi
# Stock inittab files often contain commented-out getty examples, so only treat this
# as an armed sign when there is NO active getty line left at all.
if [ -f /etc/inittab ] && grep -q getty /etc/inittab && ! grep -q '^[^#].*getty' /etc/inittab; then
  say "/etc/inittab getty lines" "ALL COMMENTED OUT  <-- review (armed kit does this)"
  [ $FOUND -eq 1 ] && ARMED=1
fi

# 3. Running process / network
# Match the process NAME exactly (a command line merely mentioning c4diagd must not count)
kitproc() { pidof c4diagd >/dev/null 2>&1 && return 0
  for p in /proc/[0-9]*; do [ "$(cat "$p/comm" 2>/dev/null)" = c4diagd ] && return 0; done; return 1; }
if kitproc; then
  say "c4diagd process" "RUNNING  <-- infection"; FOUND=1
else
  say "c4diagd process" "not running"
fi
CONN=$( (ss -tn 2>/dev/null || netstat -tn 2>/dev/null) | grep -E '44\.215\.5\.154|100\.52\.146\.110')
if [ -n "$CONN" ]; then say "Connections to attacker IPs" "PRESENT  <-- infection"; echo "$CONN"; FOUND=1
else say "Connections to attacker IPs" "none"; fi

# 4. Recovery partition (mounted read-only if not already mounted)
MNT=""
if mountpoint -q /recovery 2>/dev/null; then MNT=/recovery
elif [ -b /dev/mmcblk0p2 ]; then
  # ro,noload stops ext4 replaying its journal (a write); fall back to plain ro for FAT
  mkdir -p /tmp/c4chk_rec && { mount -o ro,noload /dev/mmcblk0p2 /tmp/c4chk_rec 2>/dev/null || mount -o ro /dev/mmcblk0p2 /tmp/c4chk_rec 2>/dev/null; } && MNT=/tmp/c4chk_rec
fi
if [ -n "$MNT" ]; then
  if [ -d "$MNT/c4diag" ]; then say "recovery/c4diag stash" "PRESENT  <-- unit was LOCKED at some point"; ARMED=1; else say "recovery/c4diag stash" "not found"; fi
  if [ -f "$MNT/recovery.scr" ]; then
    H=$(sha256sum "$MNT/recovery.scr" | cut -d' ' -f1)
    if [ "$H" = "d1aca5e82cf0b2cd8c50d1fac388a74852767cdf524f3a067d5b463e4212ddbf" ]; then
      say "recovery.scr" "HIJACKED  <-- ARMED"; ARMED=1
    else say "recovery.scr" "not the known hijacked hash"; fi
  fi
  [ "$MNT" = /tmp/c4chk_rec ] && umount /tmp/c4chk_rec 2>/dev/null && rmdir /tmp/c4chk_rec 2>/dev/null
else
  say "recovery partition" "could not check (layout differs on this model; check by hand)"
fi

# 5. Signs of attacker access
if grep -qi '^PasswordAuthentication[[:space:]]*yes' /etc/ssh/sshd_config 2>/dev/null; then
  say "sshd_config PasswordAuthentication" "YES  <-- review (attacker enabled this)"
else say "sshd_config PasswordAuthentication" "not enabled in the file"; fi
# The running sshd keeps the settings it started with. If the file changed after sshd
# started, password login may still be accepted. (The launchers also test this from outside.)
SSHD_PID=$(pidof sshd 2>/dev/null | tr ' ' '\n' | sort -n | head -1)
if [ -n "$SSHD_PID" ] && [ -f /etc/ssh/sshd_config ]; then
  BTIME=$(awk '/^btime/{print $2}' /proc/stat); HZ=$(getconf CLK_TCK 2>/dev/null || echo 100)
  ST=$(awk '{print $22}' /proc/$SSHD_PID/stat 2>/dev/null)
  if [ -n "$ST" ] && [ -n "$BTIME" ]; then
    SSHD_START=$(( BTIME + ST / HZ )); CFG=$(mtime /etc/ssh/sshd_config)
    say "sshd started" "$(fmt $SSHD_START)"; say "sshd_config last changed" "$(fmt $CFG)"
    [ -n "$CFG" ] && [ "$CFG" -gt "$SSHD_START" ] && say "  running sshd" "STARTED BEFORE CONFIG CHANGE  <-- may still accept passwords; restart sshd"
  fi
fi
for k in /etc/ssh/ssh_c4_ca.pub /etc/ssh/root_principals /etc/ssh/revoked-keys.krl; do
  [ -e "$k" ] && say "$k" "present (good)" || say "$k" "missing (armed kit deletes it; may be normal on older OS)"
done
for K in "$ROOTHOME/.ssh/authorized_keys" /root/.ssh/authorized_keys /home/root/.ssh/authorized_keys; do
  [ -f "$K" ] || continue
  case " $SEENK " in *" $K "*) continue;; esac; SEENK="$SEENK $K"
  echo "--- $K (review for unknown keys; modified $(fmt "$(mtime "$K")")) ---"; cat "$K"
done
[ -z "$SEENK" ] && echo "--- no root authorized_keys file found (home: $ROOTHOME) ---"

# 6. Evidence in logs (past infection, even after removal)
# Count only lines written BY the c4diagd program, not Control4's "Maintain - c4diagd"
# cleanup script, which ran on every controller.
if [ -z "$LOGFILES" ]; then say "Log files found under /var/log" "none"; else say "Log files found under /var/log" "$(echo $LOGFILES | wc -w)"; fi
LOGHITS=$(readlogs | grep -E 'c4diagd\[[0-9]+\]|\[c4diagd [0-9]|c4diagd starting|NOT ARMED' | grep -vi maintain | wc -l)
LOGHITS=$(echo $LOGHITS)
say "Log lines written by c4diagd" "${LOGHITS:-0}"
[ "${LOGHITS:-0}" -gt 0 ] && CLEANED=1
# Root password logins arriving through the Remote Access relay show as from 127.0.0.1
RELAYPW=$(readlogs | grep -iE 'Accepted password for root from 127\.0\.0\.1' | wc -l); RELAYPW=$(echo $RELAYPW)
say "Root PASSWORD logins from 127.0.0.1" "${RELAYPW:-0}"
if [ "${RELAYPW:-0}" -gt 0 ]; then
  CLEANED=1; echo "  (relay-tunnelled password logins match the attack pattern; first/last:)"
  readlogs | grep -iE 'Accepted password for root from 127\.0\.0\.1' | sed -n '1p;$p'
fi
OLDEST=$(for f in $LOGFILES; do echo "$(mtime "$f") $f"; done | sort -n | head -1 | cut -d' ' -f2-)
[ -n "$OLDEST" ] && say "Oldest log file kept" "$(fmt "$(mtime "$OLDEST")") (no log evidence before this)"

# 6b. Directory timestamps. The kit installed into /usr/local and the Control4 cleanup
# (Nov 23 ~06:30 UTC) removed files from /usr/local/bin and /opt, changing their times.
# OS updates and other installs also change these, so on their own they are only a hint.
for D in /usr/local /usr/local/bin /opt /opt/.c4diag; do
  [ -e "$D" ] || continue; T=$(mtime "$D"); [ -z "$T" ] && continue
  if [ "$T" -ge "$WIN_START" ] && [ "$T" -lt "$WIN_END" ]; then
    say "$D modified" "$(fmt $T)  <-- inside Nov 19-24 window"
    case "$D" in /usr/local|/usr/local/bin) TIMEHINT=1 ;; esac
  else say "$D modified" "$(fmt $T)"; fi
done

# 7. Control4 cleanup and certificate patch
if [ -f /mnt/internal/rms_maint.log ]; then say "rms_maint.log" "$(tail -1 /mnt/internal/rms_maint.log)"; else say "rms_maint.log" "not present (removed by later cleanup or never ran)"; fi
DPKGL=$(dpkg -l 2>/dev/null)
if [ -z "$DPKGL" ]; then say "patch-replace-cert package" "unknown (dpkg not usable; check Composer System Info)"
elif echo "$DPKGL" | grep -q patch-replace-cert; then say "patch-replace-cert package" "INSTALLED"
else say "patch-replace-cert package" "not installed"; fi
for c in /opt/control4/etc/ssl/certs/api.pem /etc/openvpn/api.pem; do
  [ -e "$c" ] && say "api.pem issuer" "$(openssl x509 -in "$c" -noout -issuer 2>/dev/null | sed 's/.*CN *= *//')" && break
done

# 8. For review: other builds may use different names. Nothing here changes the result.
echo
echo "--- For review (other kit builds may use different names) ---"
echo "Files in /usr/local/bin:"; ls -la /usr/local/bin 2>/dev/null
echo "Hidden folders in /opt:"; ls -a /opt 2>/dev/null | grep '^\.[^.]'
echo "Non-standard inittab respawn lines:"; grep respawn /etc/inittab 2>/dev/null | grep -v -E 'getty|^#'
echo "Files changed since Nov 15, 2026 in /etc /usr/local /opt /root /home/root:"
find /etc /usr/local /opt /root /home/root -xdev -type f -mtime -$DAYS 2>/dev/null | grep -v -E '^/opt/control4/.*\.(db|log)$' | head -60

echo
echo "=== RESULT ==="
if [ $ARMED -eq 1 ]; then
  echo "ARMED / LOCKED INDICATORS FOUND. Do not reboot, kill processes or factory reset. Escalate to Control4."
elif [ $FOUND -eq 1 ]; then
  echo "INFECTED (staged). Copy evidence off, then remove per the guide or request Control4 cleanup."
elif [ $CLEANED -eq 1 ]; then
  echo "PREVIOUSLY INFECTED, NOW CLEAN. Kit removed or masked, but traces show it was here (logs, relay logins or directory times above). Review access and keep the logs."
elif [ $TIMEHINT -eq 1 ]; then
  echo "POSSIBLE PAST INFECTION (timestamps only). /usr/local changed during Nov 19-24, matching kit install/cleanup times, but no log evidence. An OS update that week can also cause this. Confirm with firewall logs or a controller snapshot."
else
  echo "NO INDICATORS FOUND. (Logs only go back to the oldest log date above; also check firewall logs.)"
fi
