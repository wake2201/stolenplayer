# Direktverbindung über das Internet einrichten (TS2-Multiplayer 0.12.0)

Diese Anleitung richtet sich an den **Host** (Spieler 1). Der Mitspieler (Spieler 2) braucht keine Router-Einstellungen.

Es gibt genau einen Weg: Der Router des Hosts leitet **einen einzigen UDP-Port (Standard 27960)** an den Host-PC weiter.
UPnP, DMZ ("Exposed Host"), VPN, Relay-Server oder Port-Bereiche werden **nicht** verwendet und sind nicht nötig.

## Was der Mitspieler vom Host bekommt

| Angabe | Woher |
| --- | --- |
| Adresse | öffentliche IPv4-Adresse des Host-Anschlusses (Router-Übersichtsseite) oder ein DynDNS-Name |
| Port | 27960 (oder der im F8-Menü gewählte Port), UDP |
| Beitrittscode | wird nach **F8 → Hosten starten** im Menü angezeigt (Format `XXXXX-XXXXX-XXXXX-XXXXX-XXXXX`) |

Der Beitrittscode ist **bei jedem Host-Start neu** und gilt nur für diese Sitzung. Er gehört nicht in öffentliche
Chats oder Screenshots. Es kann immer nur **ein** Mitspieler beitreten; weitere Verbindungsversuche werden abgelehnt.

**Sicherheitshinweis:** Der Beitrittscode verhindert, dass Fremde beitreten (Challenge-Response mit HMAC-SHA256,
Schutz gegen Wiederholung, begrenzte Fehlversuche). Der Spielverkehr selbst ist **nicht verschlüsselt** – wer die
Datenpakete zwischen euch mitlesen kann (z. B. im selben WLAN), sieht Spielzustände. Passwörter oder Konten werden
nie übertragen.

## Schritt 1: Feste lokale IP für den Host-PC (DHCP-Reservierung)

Die Portweiterleitung zeigt auf eine lokale Adresse (z. B. `192.168.178.20`). Damit sie nach einem Neustart gleich
bleibt, im Router eine **DHCP-Reservierung** anlegen:

1. Lokale Adresse herausfinden: `ipconfig` in der Eingabeaufforderung (IPv4-Adresse des aktiven Adapters).
2. Im Router (z. B. FRITZ!Box: *Heimnetz → Netzwerk → Gerät bearbeiten → "Diesem Netzwerkgerät immer die gleiche
   IPv4-Adresse zuweisen"*; andere Router: "DHCP-Reservierung" / "Statische Zuordnung") den Host-PC fest zuordnen.

## Schritt 2: Portweiterleitung UDP 27960

Im Router eine Freigabe anlegen:

| Feld | Wert |
| --- | --- |
| Gerät / Ziel-IP | der Host-PC (Adresse aus Schritt 1) |
| Protokoll | **UDP** (nicht TCP) |
| Port extern | 27960 |
| Port intern | 27960 |

FRITZ!Box: *Internet → Freigaben → Portfreigaben → Gerät für Freigaben hinzufügen → Neue Freigabe → Portfreigabe,
Protokoll UDP, Port 27960*. Andere Router nennen das "Port Forwarding", "Virtueller Server" oder "NAT-Regel".
Keine Port-Bereiche, kein "Exposed Host", kein UPnP einschalten.

Wurde im F8-Menü (oder in `BepInEx\config\com.ts2mp.multiplayer.cfg`, `Network.HostPort`) ein anderer Port gewählt,
genau diesen Port weiterleiten und dem Mitspieler mitteilen.

## Schritt 3: Windows-Firewall

Beim ersten **Hosten** fragt Windows meist, ob "Thief Simulator 2" im Netzwerk kommunizieren darf.
Für **private Netzwerke** zulassen. Wurde die Abfrage weggeklickt:

*Windows-Sicherheit → Firewall- & Netzwerkschutz → Zugriff von App durch Firewall zulassen → Einstellungen ändern →
"Thief Simulator 2" suchen bzw. "Andere App zulassen" → `Thief Simulator 2.exe` aus dem Spielordner → Haken bei
"Privat" (und "Öffentlich" nur, wenn das Heimnetz als öffentlich eingestuft ist).*

Eine eigene Firewall-Software (Antivirus-Suite) muss UDP 27960 für das Spiel ebenfalls erlauben.

## Schritt 4: Öffentliche Adresse prüfen – CGNAT und DS-Lite

Portweiterleitung funktioniert nur, wenn der Anschluss eine **eigene öffentliche IPv4-Adresse** hat.

1. Die WAN-/Internet-IPv4-Adresse in der Router-Übersicht ablesen (FRITZ!Box: *Übersicht* bzw.
   *Internet → Online-Monitor*).
2. Mit der Adresse vergleichen, die eine Webseite wie "Wie ist meine IP" anzeigt.

| Router zeigt | Bedeutung |
| --- | --- |
| dieselbe Adresse wie die Webseite | eigene öffentliche IPv4 – Direktverbindung möglich |
| `100.64.x.x` bis `100.127.x.x` | **CGNAT** (Carrier-Grade NAT): die IPv4 teilt sich der Anschluss mit anderen Kunden |
| `10.x.x.x`, `172.16–31.x.x`, `192.168.x.x` | ein weiterer Router davor (Doppel-NAT) oder CGNAT |
| nur IPv6, IPv4 "über DS-Lite" / "AFTR" | **DS-Lite**: IPv4 läuft über einen Tunnel des Anbieters, es gibt keine eigene öffentliche IPv4 |

**Bei CGNAT oder DS-Lite kann von außen niemand per IPv4 auf den Host zugreifen** – eine Portweiterleitung im eigenen
Router hilft dann nicht. Diese Version unterstützt nur IPv4. Möglichkeiten:

- Beim Internetanbieter eine **öffentliche IPv4-Adresse** bzw. "Dual Stack statt DS-Lite" beantragen (bei vielen
  Anbietern kostenlos oder gegen kleinen Aufpreis möglich).
- Den Mitspieler hosten lassen, falls **dessen** Anschluss eine öffentliche IPv4 hat.

Bei **Doppel-NAT** (zwei Router hintereinander, z. B. Kabelmodem + eigener Router) muss die Weiterleitung auf beiden
Geräten eingerichtet werden – oder das vordere Gerät in den Bridge-Modus versetzt werden.

## Schritt 5: Spielen

1. Host: eigenen Spielstand laden, in der Welt **F8 → Hosten starten**. Adresse, Port und Beitrittscode an den
   Mitspieler schicken ("Code kopieren" legt ihn in die Zwischenablage).
2. Mitspieler: im **Hauptmenü** F8 → Adresse, Port, Beitrittscode eingeben → **Verbinden**.
3. Beide müssen dieselbe Mod-Version installiert haben (dasselbe Release-ZIP).

Im selben Heimnetz kann der Mitspieler statt der öffentlichen Adresse direkt die **lokale Adresse** des Host-PCs
verwenden (dann ist keine Portweiterleitung nötig).

Eine DynDNS-Adresse (z. B. von MyFRITZ! oder einem DynDNS-Dienst) kann statt der IPv4-Adresse eingegeben werden; sie
muss auf die aktuelle öffentliche IPv4 zeigen.

## Fehlerbehebung

Die Protokolle liegen in `%LOCALAPPDATA%\TS2MP\logs\` (ein Ordner pro Spielstart, Datei `plugin.log`). Der
Beitrittscode steht in keinem Protokoll. Das F8-Menü zeigt die meisten Fehler direkt an.

| Symptom | Wahrscheinliche Ursache | Lösung |
| --- | --- | --- |
| F8: "UDP-Port 27960 ist belegt oder blockiert" | anderes Programm/Spiel nutzt den Port | Programm schließen; oder im F8-Menü einen anderen Port wählen und diesen weiterleiten |
| F8: "Ungültiger Beitrittscode" | Tippfehler (der Code enthält zwei Prüfzeichen) | Code erneut vom Host-Bildschirm abschreiben oder kopieren |
| F8: "Zeitüberschreitung – Host nicht erreichbar" | falsche Adresse/Port, Portweiterleitung fehlt, DynDNS-Name falsch, CGNAT/DS-Lite | Schritte 2–4 prüfen, IPv4-Adresse direkt verwenden |
| F8: "Host ist noch nicht bereit" | der Host lädt gerade (Spielstand, Reise) | warten – es wird automatisch erneut versucht |
| F8: Beitreten ausgegraut, "…zuerst zum Hauptmenü zurückkehren" | du bist in einem laufenden Spiel | ins Hauptmenü wechseln, dann F8 |
| F8: Hosten ausgegraut | Hauptmenü, Spielstand noch nicht geladen, oder das Spiel lief schon als Spieler 2 | Spielstand laden; nach einer Spieler-2-Sitzung das Spiel neu starten |
| Spieler 2 verbindet sich nie, `plugin.log` zeigt nur `CONNECT attempt ...` | Portweiterleitung fehlt/falsch, falsches Protokoll (TCP statt UDP), Firewall, CGNAT/DS-Lite, falsche Adresse | Schritte 2–4 prüfen; im selben Netz zuerst mit der lokalen Adresse testen |
| `CONNECT_FAILED_FATAL: host rejected the connection with AuthFailed` | falscher oder alter Beitrittscode (Host wurde neu gestartet) | aktuellen Code vom Host verwenden, im Hauptmenü erneut F8 → Verbinden |
| `... with RateLimited` | zu viele Fehlversuche von dieser Adresse | 2 Minuten warten, dann mit dem richtigen Code erneut versuchen |
| `... with SessionFull` | es ist bereits ein Mitspieler verbunden (oder ein Beitritt läuft gerade) | nur ein Mitspieler möglich; ggf. nach einigen Sekunden erneut |
| `... with ModVersionMismatch` / `ProtocolMismatch` | unterschiedliche Mod-Versionen | beide installieren dasselbe Release-ZIP |
| `... with GameVersionMismatch` | unterschiedliche Spielversionen | beide aktualisieren das Spiel auf dieselbe Version |
| `HOST_NOT_AUTHENTICATED` | an der Adresse läuft ein anderer Host (oder ein Host ohne diesen Code) | Adresse und Port prüfen |
| Verbindung bricht nach einiger Zeit ab, `DISCONNECT ... Timeout` | Internet-Unterbrechung, WLAN-Aussetzer, Router-Neustart | Spieler 2 verbindet sich automatisch neu, solange der Host läuft; sonst im Hauptmenü erneut F8 → Verbinden |
| Funktioniert im Heimnetz, aber nicht von außen | Portweiterleitung, CGNAT/DS-Lite oder Doppel-NAT | Schritt 2 und Schritt 4 |

Hinweis: Diese Version wurde nur automatisiert auf einem PC getestet. Ein Test über das Internet zwischen zwei
getrennten PCs steht noch aus.
