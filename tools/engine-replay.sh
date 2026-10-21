#!/usr/bin/env bash
# Replays the battle server's engine recordings offline against the engine folder on this host, and writes
# replay-report.txt: where each mirror board first stopped following the match, and what it was holding.
#
#   tools/engine-replay.sh [ref]
#
# Run on the host that runs the battle container. Needs docker. Env (defaults are the ConstellationCafe server):
#   OV_HOME     folder with .env, engine/ and service/      (default /home/elaina/dockers/OpenVerseBE)
#   OV_ENGINE   engine folder                               (default $OV_HOME/engine)
#   OV_DATA     folder with card_master_full.csv.gz         (default OVBE_DATA_DIR from .env, else $OV_HOME/data)
#   OV_OUT      where recordings and the report land        (default ~/ov-replay)
# The engine folder holds the client's own DLLs: it is only mounted read-only, never copied anywhere else.
set -euo pipefail
REF=${1:-main}
OV_HOME=${OV_HOME:-/home/elaina/dockers/OpenVerseBE}
OV_ENGINE=${OV_ENGINE:-$OV_HOME/engine}
if [ -z "${OV_DATA:-}" ]; then
  OV_DATA=$(grep -E '^OVBE_DATA_DIR=' "$OV_HOME/.env" 2>/dev/null | tail -1 | cut -d= -f2- || true)
  OV_DATA=${OV_DATA:-$OV_HOME/data}
fi
OV_OUT=${OV_OUT:-$HOME/ov-replay}

[ -f "$OV_ENGINE/Assembly-CSharp.dll" ] || { echo "no engine at $OV_ENGINE"; exit 1; }
[ -f "$OV_DATA/card_master_full.csv.gz" ] || { echo "no card_master_full.csv.gz in $OV_DATA (set OV_DATA)"; exit 1; }

echo "== copying recordings out of the battle container"
rm -rf "$OV_OUT"; mkdir -p "$OV_OUT"
if ( cd "$OV_HOME/service" && docker compose --env-file ../.env cp battle:/tmp/ov-engine-rec/. "$OV_OUT/" ) 2>/dev/null; then
  ls -1 "$OV_OUT"/*.jsonl 2>/dev/null | sed 's/^/   /' || echo "   (none)"
else
  echo "   no recordings yet: only the shipped capture (CaptureFollowTests) is replayed. The battle server records once it"
  echo "   runs a build that has EngineRecorder and a match has been played"
fi

echo "== building OpenVerse ($REF) with the engine and replaying (takes a few minutes)"
docker run --rm \
  -v "$OV_ENGINE":/engine:ro -v "$OV_DATA":/data:ro -v "$OV_OUT":/rec \
  -e OPENVERSE_ENGINE_DIR=/engine/ -e OPENVERSE_REPLAY_DIR=/rec -e REF="$REF" \
  mcr.microsoft.com/dotnet/sdk:10.0 bash -c '
    set -e
    git clone -q --depth 1 -b "$REF" https://github.com/ConstellationCafe/OpenVerse.git /src
    mkdir -p /src/release/server/data && cp /data/card_master_full.csv.gz /src/release/server/data/
    cd /src
    dotnet test tests/OpenVerse.Tests/OpenVerse.Tests.csproj -c Release -nologo \
      --filter "FullyQualifiedName~EngineReplayTests|FullyQualifiedName~CaptureFollowTests" \
      --logger "console;verbosity=detailed" > /rec/test-output.txt 2>&1 || true
    grep -E "error|relayed rows|unapplied|^ +[0-9]+x |Passed!|Failed!|Total tests" /rec/test-output.txt | head -40'

echo
if [ -f "$OV_OUT/replay-report.txt" ]; then
  echo "report: $OV_OUT/replay-report.txt"
  grep -E "^## |unapplied rows" "$OV_OUT/replay-report.txt" || true
else
  echo "no report was written - see $OV_OUT/test-output.txt"
fi
