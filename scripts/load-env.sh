# Sourced by the other scripts. Exports each KEY=VALUE line of .env literally (no shell evaluation),
# because values like CONNECTION_STRING contain spaces and semicolons.
load_env() {
  local file="$1" key value
  while IFS= read -r line || [ -n "$line" ]; do
    case "$line" in ''|\#*) continue ;; esac
    key="${line%%=*}"
    value="${line#*=}"
    export "$key=$value"
  done < "$file"
}
