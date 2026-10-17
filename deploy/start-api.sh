#!/bin/sh
set -eu

database_connection_file=${CINEKROS_DATABASE_CONNECTION_STRING_FILE:-}
gemini_api_key_file=${CINEKROS_GEMINI_API_KEY_FILE:-}

if [ "${ASPNETCORE_ENVIRONMENT:-}" != "Production" ] \
    || [ "${CINEKROS_RECOMMENDATION_MODE:-}" != "real" ] \
    || [ "${CINEKROS_RUNTIME_PROFILE:-}" != "actor-v6-production" ]; then
    echo "Production API requires the real actor-v6-production runtime profile." >&2
    exit 1
fi

if [ -z "$database_connection_file" ] || [ ! -r "$database_connection_file" ]; then
    echo "Required database connection secret file is unavailable." >&2
    exit 1
fi
if [ -z "$gemini_api_key_file" ] || [ ! -r "$gemini_api_key_file" ]; then
    echo "Required Gemini API key secret file is unavailable." >&2
    exit 1
fi

DATABASE_CONNECTION_STRING=$(cat "$database_connection_file")
GEMINI_API_KEY=$(cat "$gemini_api_key_file")
if [ -z "$DATABASE_CONNECTION_STRING" ] || [ -z "$GEMINI_API_KEY" ]; then
    echo "A required application secret file is empty." >&2
    exit 1
fi
export DATABASE_CONNECTION_STRING GEMINI_API_KEY

exec dotnet "$@"
