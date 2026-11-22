#!/usr/bin/env bash
# Prints JSON with the image tags of the running Container Apps applications and jobs in a resource group, e.g.
#   {"inventory-api":"3f2a9c1…","inventory-worker":"3f2a9c1…","readmodels-api":"b71d0e4…"}
# The result goes into the IMAGE_TAGS variable read by infra/parameters/*.bicepparam.
# WHY: an infrastructure deployment must not change application versions (see the comment at "images" in apps.bicep).
# Usage: current-image-tags.sh <resource-group>
set -euo pipefail

resource_group="$1"

{
  az containerapp list --resource-group "$resource_group" \
    --query "[].properties.template.containers[0].image" --output tsv
  az containerapp job list --resource-group "$resource_group" \
    --query "[].properties.template.containers[0].image" --output tsv
} | jq --raw-input --slurp --compact-output '
    split("\n")
    | map(select(length > 0) | split("/") | last | split(":") | { (.[0]): .[1] })
    | add // {}'
