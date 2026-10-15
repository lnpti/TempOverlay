# TempOverlay

Mostra a temperatura sobreposta a qualquer programa do Windows (útil em softwares sem campo de clima, como o Mixing Station).

- **Fonte:** cidade escolhida nas configurações (Open-Meteo, sem chave) **ou** um arquivo `.txt` contendo só um número (ex.: `27,5`).
- Painel sem borda, sempre por cima, com opção de travar a posição (cliques passam por cima).
- Aparência configurável com prévia ao vivo; ícone na bandeja; iniciar com o Windows.
- Config em `%APPDATA%\TempOverlay\config.json`.

Instalador: veja a página de [Releases](../../releases). App nativo (.NET Framework, já incluso no Windows 10/11), ~5 MB de RAM. Para compilar: `.build.ps1` (requer apenas o Inno Setup 6).
