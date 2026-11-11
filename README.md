# Luigi

Luigi ist ein persönlicher KI-Assistent, der auf dem PC wohnt.

## Herunterladen

Die neueste **Luigi.exe** gibt es unter [Releases](../../releases/latest).
Einfach herunterladen und per Doppelklick starten. Falls Windows warnt: *Weitere Informationen* → *Trotzdem ausführen*.

Nach jeder Änderung baut GitHub die Luigi.exe automatisch neu.

## KI-Schlüssel

In Luigi unten links auf **Einstellungen** klicken und die Schlüssel einfügen:

- Groq: console.groq.com/keys
- OpenRouter: openrouter.ai/settings/keys
- Google AI Studio: aistudio.google.com/apikey

Die Schlüssel bleiben nur auf dem eigenen PC gespeichert.

## Aufbau

- `index.html` – die Oberfläche (Chat, Planungsmodus mit Whiteboard, Modelle, Einstellungen)
- `app/` – das Windows-Programm drumherum
- `.github/workflows/build.yml` – baut die Luigi.exe automatisch
