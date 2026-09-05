#!/usr/bin/env bash
# 稳定性测试：开启加速后运行本脚本，周期性通过代理请求 github.com，
# 输出实时结果与最终统计（成功率 / 平均 / P95 延迟）。
# 用法: scripts/stability-test.sh [时长分钟，默认30] [间隔秒，默认30]
set -u
DURATION_MIN="${1:-30}"
INTERVAL="${2:-30}"
TARGET="${TARGET:-https://github.com}"
END=$((SECONDS + DURATION_MIN * 60))
CSV="${CSV:-/tmp/flashgithub-stability.csv}"

echo "时间      HTTP  耗时     累计成功率"
echo "$CSV" > "$CSV"
TOTAL=0; OK=0; SUM_MS=0
while [ "$SECONDS" -lt "$END" ]; do
  TS=$(date +%H:%M:%S)
  RES=$(curl -s --noproxy '*' -o /dev/null -w "%{http_code} %{time_total}" --max-time 20 "$TARGET" 2>/dev/null || echo "000 20")
  CODE="${RES%% *}"; SEC="${RES#* }"
  MS=$(awk -v s="$SEC" 'BEGIN{printf "%d", s*1000}')
  OKN=0
  case "$CODE" in 200|301) OKN=1 ;; esac
  TOTAL=$((TOTAL+1)); OK=$((OK+OKN)); SUM_MS=$((SUM_MS+MS))
  PCT=$((OK * 100 / TOTAL))
  echo "$TS  $CODE  ${MS}ms  ${PCT}%"
  echo "$(date +%s),$OKN,$CODE,$MS" >> "$CSV"
  sleep "$INTERVAL"
done

echo "==================== 统计 ===================="
tail -n +2 "$CSV" | cut -d, -f2 > /tmp/fg-st-ok
tail -n +2 "$CSV" | cut -d, -f4 | sort -n > /tmp/fg-st-lat
TOTAL=$(grep -c . /tmp/fg-st-ok)
OK=$(grep -c '^1$' /tmp/fg-st-ok || true)
if [ "$TOTAL" -gt 0 ]; then
  AVG=$(awk '{s+=$1} END{if(NR) printf "%d", s/NR}' /tmp/fg-st-lat)
  P50=$(sed -n "$(( TOTAL / 2 < 1 ? 1 : TOTAL / 2 ))p" /tmp/fg-st-lat)
  P95IDX=$(( TOTAL * 95 / 100 )); [ "$P95IDX" -lt 1 ] && P95IDX=1
  P95=$(sed -n "${P95IDX}p" /tmp/fg-st-lat)
  MAX=$(tail -1 /tmp/fg-st-lat)
  awk -v t="$TOTAL" -v o="$OK" -v a="$AVG" -v p50="$P50" -v p95="$P95" -v m="$MAX" \
    'BEGIN{printf "请求数: %s  成功: %s  成功率: %.1f%%\n平均: %s ms  P50: %s ms  P95: %s ms  最大: %s ms\n", t,o,o*100/t,a,p50,p95,m}'
fi
echo "明细 CSV: $CSV"
