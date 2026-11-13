# Luigi

<p align="center"><b>Dein eigener KI-Assistent für Windows, der auf deinem PC wohnt.</b></p>

Luigi ist ein Chatfenster für deinen PC: Du redest mit einer KI, und sie kann für dich
Ordner anlegen, Dateien suchen und sortieren, Webseiten lesen, Programme öffnen und
zusammen mit dir auf einem Whiteboard planen. Alles bleibt auf deinem Computer.

## Herunterladen

Die neueste **Luigi.exe** gibt es unter [Releases](../../releases/latest).
Herunterladen, Doppelklick, fertig. Es wird nichts installiert.

Falls Windows warnt: *Weitere Informationen* → *Trotzdem ausführen*.
Die Warnung kommt, weil die Datei neu ist und keine teure Signatur hat.

Du brauchst Windows 10 oder 11. Microsoft Edge WebView2 ist dort schon dabei;
falls nicht, sagt Luigi beim Start Bescheid.

## Erster Start

Luigi fragt dich nach deinem Namen und einem KI-Schlüssel. Der Schlüssel ist kostenlos
und bleibt nur auf deinem PC:

| Anbieter | Schlüssel holen | gut für |
|---|---|---|
| Groq | console.groq.com/keys | am schnellsten |
| OpenRouter | openrouter.ai/settings/keys | viele kostenlose Modelle |
| Google AI Studio | aistudio.google.com/apikey | Gemini |

Einer reicht. Wenn du mehrere einträgst, springt Luigi automatisch um, falls einer gerade streikt.

## Was Luigi kann

- **Dateien und Ordner**: anlegen, ansehen, lesen, suchen, verschieben, kopieren, umbenennen,
  zippen, aufräumen und in den Papierkorb legen. Jede Änderung bestätigst du per Klick.
- **Internet**: suchen, Webseiten lesen und zusammenfassen, Seiten öffnen
- **Programme** öffnen, Text in die Zwischenablage legen, PC-Infos abfragen, Erinnerungen stellen
- **Planungsmodus**: ein Whiteboard, auf das Luigi selbst Mindmaps und Pläne malt, während du mitmalst
- **Anhänge**: Textdateien, Code und Bilder in den Chat ziehen
- **Merkzettel**: Dinge, die Luigi dauerhaft über dich wissen soll
- **Luigi Eigen 0.0.0.1**: eine eigene kleine KI, die auf dem PC lernt. Sie startet völlig
  leer und kann nur, was man ihr in der **Schule** beibringt. Dahinter steckt ein kleines
  neuronales Netz, das im Programm selbst trainiert wird – ohne Internet, ohne Schlüssel.

Löschen ohne Papierkorb, Systemdateien und das Starten von Programmen aus dem Chat heraus
kann Luigi nicht.

## Updates

Luigi holt sich seine Oberfläche beim Start selbst und aktualisiert sie ohne neuen Download.
Nur wenn sich das Programm selbst ändert, bietet er oben ein Update an, das ein Klick erledigt.

## Für Entwickler

- `index.html` – die komplette Oberfläche (Chat, Planungsmodus, Einstellungen), eine Datei ohne Abhängigkeiten
- `app/` – das Windows-Programm drumherum (C#, WinForms, WebView2)
- `.github/workflows/build.yml` – baut die Luigi.exe automatisch bei jedem Push

Die Zahl in `<meta name="luigi-ui">` ist die Oberflächen-Version. Wird sie erhöht,
laden alle laufenden Luigis die neue Oberfläche beim nächsten Start.
