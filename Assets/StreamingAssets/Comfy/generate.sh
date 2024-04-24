#!/bin/bash

container=""
positive="ultra detailed, surreal, abstract, conceptual, masterpiece"
negative="blurry, smooth, simple, plain, cute, naked, nude, nudity, sexy"
width=1152
height=896
steps=10
guidance=2.5
model="TURBO_DreamShaper.safetensors"
sampler="dpmpp_sde_gpu"
scheduler="karras"
prompt=""
prefix="background"

while [ $# -gt 0 ]; do
    key="${1#--}"
    value="$2"
    declare -p "$key" &>/dev/null || { echo "Unknown option '$key'." >&2; exit 1; }
    [ "$value" ] || { echo "Missing value for '$value'." >&2; exit 1; }
    declare -g "$key=$value"
    shift 2
done

[ -z "$container" ] && { echo "Missing container identifier." >&2; exit 1; }

wait=0.5
timeout=50
folder="$(realpath $(dirname $0))"
output="$folder/output"
pattern="^$prefix"
graph=$(export prompt="$prompt" forbid="$forbid" seed=$RANDOM positive="$positive" negative="$negative" width="$width" height="$height" steps="$steps" guidance="$guidance" model="$model" sampler="$sampler" scheduler="$scheduler" prefix="$prefix" && cat "$folder/template.json" | envsubst | tr '\n' ' ') || exit $?
echo "$graph" > "$folder/graph.json"
old=$(ls -t "$output" | grep "$pattern" | head -n 1)
response=$(docker exec "$container" curl localhost:8188/prompt --silent --data "$graph") || exit $?

# Waiting for image generation.
time=$(date +%s)
while [ $(($(date +%s) - time)) -le "$timeout" ]; do
    new=$(ls -t "$output" | grep "$pattern" | head -n 1)
    if [ "$old" != "$new" ]; then
        echo "$output/$new"
        exit 0
    fi
    sleep "$wait"
done
exit 1