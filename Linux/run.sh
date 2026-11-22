#!/bin/sh
set -eu
cd -- "$(dirname -- "$0")"
if [ -f ./EmiConverter ]; then
    exec ./EmiConverter "$@"
fi
exec dotnet ./EmiConverter.dll "$@"
