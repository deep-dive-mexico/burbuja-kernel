#!/bin/sh
# Stands in for a real program. It says where it runs and then works until it is asked to stop.
echo "$BURBUJA_WORKLOAD started on $BURBUJA_NODE (generation $BURBUJA_GENERATION) with: $*"
trap 'echo "$BURBUJA_WORKLOAD asked to stop; finishing"; exit 0' TERM
while true; do sleep 1; done
