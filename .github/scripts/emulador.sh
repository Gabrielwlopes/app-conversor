#!/usr/bin/env bash
# Liga um Android emulado (Android 13, x86_64) no GitHub Actions e roda o autoteste nele.
# Uso: emulador.sh <pasta com o APK de teste>
# Se algo falhar, o fim do log vira uma anotação na página da execução (fácil de ler sem abrir o log inteiro).
set -uo pipefail

api=33
image="system-images;android-${api};google_apis;x86_64"
sdk="${ANDROID_SDK_ROOT:-$ANDROID_HOME}"
tools="$sdk/cmdline-tools/latest/bin"
export PATH="$sdk/platform-tools:$sdk/emulator:$PATH"
log="$PWD/emulador.log"
: > "$log"

fail() {
  echo "::error title=Emulador: $1::$(tail -c 4000 "$log" | sed ':a;N;$!ba;s/\n/%0A/g')"
  exit 1
}

echo "Instalando a imagem do Android $api…"
df -h / >> "$log"
# "yes" aceita as licenças; o código de saída que importa é o do sdkmanager (o do "yes" é sempre de erro).
yes 2> /dev/null | "$tools/sdkmanager" --install "$image" "emulator" "platform-tools" >> "$log" 2>&1
[ "${PIPESTATUS[1]}" = 0 ] || fail "sdkmanager"

echo "Criando o aparelho virtual…"
echo no | "$tools/avdmanager" create avd -n teste -k "$image" -d pixel_6 --force >> "$log" 2>&1 || fail "avdmanager"

echo "Ligando o emulador…"
emulator -avd teste -no-window -no-audio -no-boot-anim -no-snapshot -gpu swiftshader_indirect -memory 3072 -accel on \
  >> "$log" 2>&1 &
emulator_pid=$!

deadline=$(( $(date +%s) + 900 ))
until [ "$(adb shell getprop sys.boot_completed 2>/dev/null | tr -d '\r')" = "1" ]; do
  kill -0 "$emulator_pid" 2>/dev/null || fail "o emulador fechou"
  [ "$(date +%s)" -lt "$deadline" ] || fail "não ligou em 15 minutos"
  sleep 5
done
echo "Ligado em $(( $(date +%s) - deadline + 900 ))s"
adb shell settings put global window_animation_scale 0
adb shell settings put global transition_animation_scale 0
adb shell settings put global animator_duration_scale 0
adb shell input keyevent 82 # desbloqueia a tela

bash "$(dirname "$0")/autoteste.sh" "$1"
status=$?
adb emu kill > /dev/null 2>&1 || true
exit $status
