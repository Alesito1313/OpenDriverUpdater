using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;

namespace OpenDriverUpdater
{
    /// <summary>
    /// Textos que salen del lado C# (progreso de instalacion, errores, informe).
    /// Los de la interfaz estan en ui/i18n.js.
    /// Orden: en, es, de, fr, it
    /// </summary>
    static class Lang
    {
        public static readonly string[] Codes = { "en", "es", "de", "fr", "it" };
        public static string Current { get; private set; } = "en";

        static readonly Dictionary<string, string[]> T = new Dictionary<string, string[]>
        {
            ["preparing"] = new[] { "Preparing…", "Preparando…", "Wird vorbereitet…", "Préparation…", "Preparazione…" },
            ["restorePoint"] = new[] { "Creating a restore point…", "Creando punto de restauración…", "Wiederherstellungspunkt wird erstellt…", "Création d’un point de restauration…", "Creazione del punto di ripristino…" },
            ["restorePointFailed"] = new[] { "Couldn’t create a restore point (it may be turned off). Continuing…", "No se pudo crear el punto de restauración (puede estar desactivado). Continuando…", "Wiederherstellungspunkt konnte nicht erstellt werden (evtl. deaktiviert). Es geht weiter…", "Impossible de créer un point de restauration (il est peut-être désactivé). On continue…", "Impossibile creare il punto di ripristino (potrebbe essere disattivato). Si continua…" },
            ["gettingLink"] = new[] { "Getting download link: {0}", "Obteniendo enlace de descarga: {0}", "Download-Link wird abgerufen: {0}", "Récupération du lien : {0}", "Recupero del link: {0}" },
            ["downloading"] = new[] { "Downloading: {0}", "Descargando: {0}", "Wird heruntergeladen: {0}", "Téléchargement : {0}", "Download: {0}" },
            ["downloadIncomplete"] = new[] { "The download was cut off before it finished", "La descarga se cortó antes de terminar", "Der Download wurde vor dem Ende abgebrochen", "Le téléchargement s’est interrompu avant la fin", "Il download si è interrotto prima della fine" },
            ["extracting"] = new[] { "Extracting: {0}", "Extrayendo: {0}", "Wird entpackt: {0}", "Extraction : {0}", "Estrazione: {0}" },
            ["installing"] = new[] { "Installing: {0}", "Instalando: {0}", "Wird installiert: {0}", "Installation : {0}", "Installazione: {0}" },
            ["installed"] = new[] { "Installed", "Instalado correctamente", "Installiert", "Installé", "Installato" },
            ["installedReboot"] = new[] { "Installed (restart required)", "Instalado (requiere reiniciar)", "Installiert (Neustart erforderlich)", "Installé (redémarrage requis)", "Installato (riavvio necessario)" },
            ["installedWarnings"] = new[] { "Installed with warnings", "Instalado con advertencias", "Mit Warnungen installiert", "Installé avec des avertissements", "Installato con avvisi" },
            ["notApplied"] = new[] { "Windows added the driver but didn’t apply it: it doesn’t exactly match your device or Windows prefers the current one", "Windows añadió el driver pero no lo aplicó: no corresponde exactamente a tu dispositivo o prefiere el actual", "Windows hat den Treiber hinzugefügt, aber nicht angewendet: Er passt nicht genau zu deinem Gerät oder Windows bevorzugt den aktuellen", "Windows a ajouté le pilote sans l’appliquer : il ne correspond pas exactement à votre appareil ou Windows préfère l’actuel", "Windows ha aggiunto il driver ma non l’ha applicato: non corrisponde esattamente al dispositivo o Windows preferisce quello attuale" },
            ["pnputilError"] = new[] { "pnputil exited with code {0}: {1}", "pnputil terminó con código {0}: {1}", "pnputil wurde mit Code {0} beendet: {1}", "pnputil s’est terminé avec le code {0} : {1}", "pnputil è terminato con codice {0}: {1}" },
            ["noLink"] = new[] { "The catalog didn’t return a download link", "El catálogo no devolvió un enlace de descarga", "Der Katalog hat keinen Download-Link geliefert", "Le catalogue n’a pas renvoyé de lien", "Il catalogo non ha restituito un link" },
            ["noInf"] = new[] { "The package doesn’t contain any .inf file", "El paquete no contiene ningún archivo .inf", "Das Paket enthält keine .inf-Datei", "Le paquet ne contient aucun fichier .inf", "Il pacchetto non contiene file .inf" },
            ["extractFailed"] = new[] { "Couldn’t extract the package ({0})", "No se pudo extraer el paquete ({0})", "Paket konnte nicht entpackt werden ({0})", "Impossible d’extraire le paquet ({0})", "Impossibile estrarre il pacchetto ({0})" },
            ["badFormat"] = new[] { "Unsupported package format: {0}", "Formato de paquete no soportado: {0}", "Nicht unterstütztes Paketformat: {0}", "Format de paquet non pris en charge : {0}", "Formato del pacchetto non supportato: {0}" },
            ["armOnly"] = new[] { "This driver is for ARM64 processors, not for your PC", "Este driver es para procesadores ARM64, no para tu PC", "Dieser Treiber ist für ARM64-Prozessoren, nicht für deinen PC", "Ce pilote est pour les processeurs ARM64, pas pour votre PC", "Questo driver è per processori ARM64, non per il tuo PC" },
            ["nvidiaNoLink"] = new[] { "NVIDIA didn’t provide a download link", "NVIDIA no proporcionó enlace de descarga", "NVIDIA hat keinen Download-Link geliefert", "NVIDIA n’a pas fourni de lien", "NVIDIA non ha fornito un link" },
            ["nvidiaWizard"] = new[] { "Opening the NVIDIA installer: follow its steps in the window that appears…", "Abriendo el instalador de NVIDIA: sigue sus pasos en la ventana que aparece…", "NVIDIA-Installer wird geöffnet: Folge den Schritten im Fenster…", "Ouverture du programme d’installation NVIDIA : suivez ses étapes…", "Apertura dell’installer NVIDIA: segui i passaggi nella finestra…" },
            ["nvidiaInstalled"] = new[] { "Installed: the card now uses driver {0}", "Instalado: la tarjeta ya usa el driver {0}", "Installiert: Die Karte nutzt jetzt Treiber {0}", "Installé : la carte utilise maintenant le pilote {0}", "Installato: la scheda ora usa il driver {0}" },
            ["checkingDriver"] = new[] { "Checking that the new driver is active…", "Comprobando que el nuevo driver está activo…", "Prüfe, ob der neue Treiber aktiv ist…", "Vérification que le nouveau pilote est actif…", "Verifica che il nuovo driver sia attivo…" },
            ["waitingInstalls"] = new[] { "Waiting for Windows to finish another driver installation…", "Esperando a que Windows termine otra instalación de drivers…", "Warte, bis Windows eine andere Treiberinstallation abschließt…", "Attente de la fin d’une autre installation de pilote…", "In attesa che Windows completi un’altra installazione di driver…" },
            ["nvidiaExit"] = new[] { "The NVIDIA installer exited with code {0} (cancelled?)", "El instalador de NVIDIA terminó con código {0} (¿se canceló?)", "Der NVIDIA-Installer wurde mit Code {0} beendet (abgebrochen?)", "Le programme NVIDIA s’est terminé avec le code {0} (annulé ?)", "L’installer NVIDIA è terminato con codice {0} (annullato?)" },
            ["nvidiaNothing"] = new[] { "NVIDIA returned no driver for this card", "NVIDIA no devolvió ningún driver para esta tarjeta", "NVIDIA hat keinen Treiber für diese Karte geliefert", "NVIDIA n’a renvoyé aucun pilote pour cette carte", "NVIDIA non ha restituito driver per questa scheda" },
            ["wuDownloadError"] = new[] { "Download error (0x{0:X8})", "Error en la descarga (0x{0:X8})", "Downloadfehler (0x{0:X8})", "Erreur de téléchargement (0x{0:X8})", "Errore di download (0x{0:X8})" },
            ["wuInstallError"] = new[] { "Installation error (0x{0:X8})", "Error en la instalación (0x{0:X8})", "Installationsfehler (0x{0:X8})", "Erreur d’installation (0x{0:X8})", "Errore di installazione (0x{0:X8})" },
            ["wuMissing"] = new[] { "Windows Update Agent not found on this system", "No se encontró el Agente de Windows Update", "Windows Update-Agent nicht gefunden", "Agent Windows Update introuvable", "Agente di Windows Update non trovato" },
            ["cancelled"] = new[] { "Cancelled", "Cancelado", "Abgebrochen", "Annulé", "Annullato" },
            ["warnWu"] = new[] { "Windows Update didn’t respond: {0}", "Windows Update no respondió: {0}", "Windows Update hat nicht geantwortet: {0}", "Windows Update n’a pas répondu : {0}", "Windows Update non ha risposto: {0}" },
            ["warnCatalog"] = new[] { "{0} Microsoft Catalog queries failed (connection)", "{0} consultas al Catálogo de Microsoft fallaron (conexión)", "{0} Abfragen an den Microsoft-Katalog sind fehlgeschlagen (Verbindung)", "{0} requêtes au catalogue Microsoft ont échoué (connexion)", "{0} richieste al Catalogo Microsoft non riuscite (connessione)" },
            ["warnNvidia"] = new[] { "NVIDIA: {0}", "NVIDIA: {0}", "NVIDIA: {0}", "NVIDIA : {0}", "NVIDIA: {0}" },
            ["scanFailed"] = new[] { "{0} Check your Internet connection.", "{0} Comprueba tu conexión a Internet.", "{0} Prüfe deine Internetverbindung.", "{0} Vérifiez votre connexion Internet.", "{0} Controlla la connessione a Internet." },
            ["installError"] = new[] { "Error during the update: {0}", "Error durante la actualización: {0}", "Fehler beim Aktualisieren: {0}", "Erreur pendant la mise à jour : {0}", "Errore durante l’aggiornamento: {0}" },
            ["exportFailed"] = new[] { "Couldn’t save the report: {0}", "No se pudo guardar el informe: {0}", "Bericht konnte nicht gespeichert werden: {0}", "Impossible d’enregistrer le rapport : {0}", "Impossibile salvare il report: {0}" },
            ["busyClose"] = new[] { "An update is in progress. Closing now isn’t recommended.\n\nClose anyway?", "Hay una actualización en curso. No es recomendable cerrar ahora.\n\n¿Cerrar de todos modos?", "Eine Aktualisierung läuft. Schließen wird nicht empfohlen.\n\nTrotzdem schließen?", "Une mise à jour est en cours. Il est déconseillé de fermer.\n\nFermer quand même ?", "È in corso un aggiornamento. Non è consigliato chiudere.\n\nChiudere comunque?" },
            ["noWebView"] = new[] { "Microsoft Edge WebView2 is missing (needed to show the interface). It comes with Windows 11 and updated Windows 10.\n\nOpen Microsoft’s download page?", "Falta Microsoft Edge WebView2 (necesario para mostrar la interfaz). Viene con Windows 11 y Windows 10 actualizado.\n\n¿Abrir la página de descarga de Microsoft?", "Microsoft Edge WebView2 fehlt (für die Oberfläche nötig). Es ist in Windows 11 und aktuellem Windows 10 enthalten.\n\nDownloadseite von Microsoft öffnen?", "Microsoft Edge WebView2 est manquant (nécessaire à l’interface). Il est inclus dans Windows 11 et Windows 10 à jour.\n\nOuvrir la page de téléchargement de Microsoft ?", "Manca Microsoft Edge WebView2 (necessario per l’interfaccia). È incluso in Windows 11 e Windows 10 aggiornato.\n\nAprire la pagina di download di Microsoft?" },
            ["uiFailed"] = new[] { "Couldn’t start the interface:\n\n{0}", "No se pudo iniciar la interfaz:\n\n{0}", "Oberfläche konnte nicht gestartet werden:\n\n{0}", "Impossible de démarrer l’interface :\n\n{0}", "Impossibile avviare l’interfaccia:\n\n{0}" },
            ["src.wu"] = new[] { "Windows Update", "Windows Update", "Windows Update", "Windows Update", "Windows Update" },
            ["src.catalog"] = new[] { "Microsoft Catalog", "Catálogo Microsoft", "Microsoft-Katalog", "Catalogue Microsoft", "Catalogo Microsoft" },
            ["src.nvidia"] = new[] { "NVIDIA (official site)", "NVIDIA (web oficial)", "NVIDIA (offizielle Website)", "NVIDIA (site officiel)", "NVIDIA (sito ufficiale)" },
            // informe
            ["rep.title"] = new[] { "OpenDriverUpdater report", "Informe de OpenDriverUpdater", "OpenDriverUpdater-Bericht", "Rapport OpenDriverUpdater", "Report di OpenDriverUpdater" },
            ["rep.status"] = new[] { "Status", "Estado", "Status", "État", "Stato" },
            ["rep.installed"] = new[] { "Installed", "Instalado", "Installiert", "Installé", "Installato" },
            ["rep.latest"] = new[] { "Latest official", "Última oficial", "Neueste offizielle", "Dernière officielle", "Ultima ufficiale" },
            ["rep.none"] = new[] { "(none)", "(ninguno)", "(keiner)", "(aucun)", "(nessuno)" },
            ["rep.candidates"] = new[] { "Versions found", "Versiones encontradas", "Gefundene Versionen", "Versions trouvées", "Versioni trovate" },
            ["rep.searched"] = new[] { "IDs searched", "IDs consultados", "Gesuchte IDs", "ID recherchés", "ID cercati" },
        };

        public static string Get(string key, params object[] args)
        {
            if (!T.TryGetValue(key, out var texts)) return key;
            int i = Array.IndexOf(Codes, Current);
            var text = texts[i < 0 ? 0 : i];
            return args.Length == 0 ? text : string.Format(text, args);
        }

        public static string SourceName(Core.Source s) =>
            Get(s == Core.Source.Nvidia ? "src.nvidia" : s == Core.Source.WindowsUpdate ? "src.wu" : "src.catalog");

        // el idioma elegido se guarda en %LOCALAPPDATA%\OpenDriverUpdater\settings.json
        static string SettingsFile => Path.Combine(Program.DataDir, "settings.json");

        public static void Load()
        {
            try
            {
                var json = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(SettingsFile));
                if (json.TryGetValue("lang", out var l) && Codes.Contains(l as string)) Current = (string)l;
            }
            catch { /* primera vez o archivo roto: ingles */ }
        }

        public static void Set(string code)
        {
            if (!Codes.Contains(code)) return;
            Current = code;
            try
            {
                Directory.CreateDirectory(Program.DataDir);
                File.WriteAllText(SettingsFile, new JavaScriptSerializer().Serialize(new Dictionary<string, object> { ["lang"] = code }));
            }
            catch { }
        }
    }
}
