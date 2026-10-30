#!/usr/bin/env bash
# Roda o autoteste do app num emulador Android (chamado pelo workflow, com o emulador já ligado).
# Uso: autoteste.sh <pasta com o APK de teste>
set -uo pipefail

package=com.gabrielwlopes.conversor
out=autoteste
mkdir -p "$out"

apk=$(find "$1" -name '*-Signed.apk' | head -n 1)
if [ -z "$apk" ]; then
  echo "::error::APK de teste não encontrado em $1"
  exit 1
fi
echo "Instalando $apk"
adb install -r "$apk" || { echo "::error::Falha ao instalar o APK no emulador"; exit 1; }

adb logcat -c
adb shell am start -W -n "$package/$package.MainActivity" --ez selftest true
sleep 10
adb exec-out screencap -p > "$out/1-durante.png"

result=timeout
for _ in $(seq 1 90); do
  log=$(adb logcat -d -s ConversorTest:I)
  if grep -q "SELFTEST PASSED" <<< "$log"; then result=passed; break; fi
  if grep -q "SELFTEST FAILED" <<< "$log"; then result=failed; break; fi
  if ! adb shell pidof "$package" > /dev/null; then result=crash; break; fi
  sleep 5
done
sleep 4
adb exec-out screencap -p > "$out/2-resultado.png"
adb shell input swipe 500 1500 500 300 300
sleep 2
adb exec-out screencap -p > "$out/3-rolado.png"

adb logcat -d > "$out/logcat.txt"
adb logcat -d -s ConversorTest:I | sed -E 's/^.*ConversorTest[^:]*: //' > "$out/resultado.txt"
echo "──── resultado ────"
cat "$out/resultado.txt"

# Um resumo por anotação (aparece na página da execução no GitHub).
summary=$(grep -E '^(OK|FALHOU|SELFTEST)' "$out/resultado.txt" | sed ':a;N;$!ba;s/\n/%0A/g')
if [ "$result" = passed ]; then
  echo "::notice title=Autoteste no emulador::$summary"
else
  echo "::error title=Autoteste no emulador ($result)::$summary"
  # Erros do app (exceções do .NET e do Android) para descobrir o motivo.
  grep -E "FATAL EXCEPTION|AndroidRuntime|monodroid|mono-rt|ConversorTest" "$out/logcat.txt" | grep -Ev "^\s*$" | head -40 \
    | sed ':a;N;$!ba;s/\n/%0A/g' | sed 's/^/::warning title=Logcat::/'
  exit 1
fi
