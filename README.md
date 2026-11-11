# KeyLayer

Rimappatura dei tasti "a layer" per Windows, in stile Vim. Programma **portable**: un solo `KeyLayer.exe` (circa 60 KB), nessuna installazione e niente runtime da scaricare (usa .NET Framework 4.x, già incluso in Windows 10/11).

## Uso

1. Copia `dist\KeyLayer.exe` in una cartella qualsiasi (anche una chiavetta USB) e avvialo.
2. Resta in esecuzione in background, con un'icona nell'area di notifica.
3. Attiva il layer con **Shift destro**: compare l'overlay con la tastiera, con i tasti rimappati in **giallo**.

### Modalità di attivazione (Impostazioni → Modalità)

| Modalità | Come funziona |
|---|---|
| **Doppio tocco** (predefinita) | Due tocchi rapidi di Shift destro accendono il layer, altri due lo spengono. Shift destro continua a funzionare normalmente per le maiuscole. |
| **Tieni premuto** | Il layer è attivo solo finché tieni premuto Shift destro. Il **primo** tasto che premi decide: se è rimappato (es. J) vale il layer; altrimenti Shift destro fa lo Shift normale (maiuscole, Shift+clic). L'overlay compare dopo circa 0,2 s, quindi non lampeggia quando scrivi una maiuscola. |
| **Tocco singolo** | Un tocco accende, un altro spegne. |

Un "tocco" vale solo se Shift destro viene premuto **da solo**: Shift+tasto e Shift+clic non contano. L'intervallo del doppio tocco si regola nel file ini (`DoubleTapMs`, predefinito 400 ms).

### Posizione dell'overlay

Con **Automatica** (predefinita) l'overlay compare sul monitor dove si trova il puntatore del mouse, lontano da esso:
- puntatore nel terzo **basso** dello schermo → overlay **in alto**;
- puntatore nel terzo **alto** → overlay **in basso**;
- puntatore nella **fascia centrale** → overlay **a destra**, oppure a sinistra se il puntatore è già vicino al bordo destro.

Se mentre è visibile ci passi sopra col mouse, o passi su un altro monitor, l'overlay si sposta. Sono disponibili anche posizioni fisse.

### Desktop virtuali

L'overlay compare sempre sul desktop virtuale su cui stai lavorando, e se cambi desktop con il layer attivo ti segue. Anche la finestra delle impostazioni si riapre sul desktop corrente, invece di riportarti su quello dove l'avevi lasciata.

### Tasti permanenti

Finché KeyLayer è in esecuzione vengono disattivati i **Tasti permanenti** e la loro scorciatoia (5 volte Shift), oltre alla scorciatoia di **Filtro tasti** (tenere premuto Shift destro per 8 secondi, che in modalità "tieni premuto" potrebbe scattare). La modifica vale solo per la sessione corrente e all'uscita vengono ripristinate le impostazioni originali. Si può disattivare questa funzione dalle Impostazioni.

## Profili

Le rimappature sono raccolte in **profili**. Ne è attivo uno alla volta, e puoi cambiarlo dalle Impostazioni o dal menu dell'icona → **Profilo**. L'overlay mostra il nome del profilo attivo.

Il profilo **Personale** (predefinito) contiene:

| Tasto | Diventa |
|---|---|
| I / J / K / L | ↑ / ← / ↓ / → |
| U / O | Pagina su / Pagina giù |
| N / M | Inizio (Home) / Fine (End) |

Funzionano anche le combinazioni: Shift + J = Shift + ← (selezione), Ctrl + J = Ctrl + ←, Shift + M = seleziona fino a fine riga.

### Profili pronti

Da Impostazioni → **«Carica profilo pronto ▾»**:

- **Vim**: per prendere confidenza con i comandi di Vim usandoli ovunque in Windows (vedi sotto);
- **Navigazione IJKL**: la mappatura del profilo Personale.

Un profilo pronto si aggiunge ai tuoi senza toccarli. Se lo ricarichi, puoi ripristinarlo alla versione originale oppure solo attivarlo così com'è. Puoi anche modificarlo come qualsiasi altro profilo.

### Profilo Vim

Il layer fa da *normal mode* e la scrittura normale da *insert mode*. I comandi sono adattati, dove possibile, ai tasti di modifica di Windows:

| Vim | Effetto in KeyLayer |
|---|---|
| `h` `j` `k` `l` | ← ↓ ↑ → |
| `w` / `e` / `b` | parola successiva / successiva / precedente (Ctrl+→ / Ctrl+←) |
| `0` / `$` (Shift+4) | inizio / fine riga |
| `g` (gg) / `G` | inizio / fine documento |
| Ctrl+`f` Ctrl+`d` / Ctrl+`b` Ctrl+`u` | pagina giù / pagina su |
| `x` / `X` | cancella carattere avanti (Canc) / indietro (Backspace) |
| `u` / Ctrl+`r` | annulla (Ctrl+Z) / ripeti (Ctrl+Y) |
| `d` (dd) / `D` | taglia la riga / cancella fino a fine riga |
| `y` (yy) | copia la riga |
| `p` / `P` | incolla sotto / sopra la riga corrente |
| `V` | seleziona la riga |
| `/` (Shift+7) · `n` · `N` | cerca (Ctrl+F) · successivo (F3) · precedente (Shift+F3) |
| `i` · `a` · `I` · `A` · `o` · `O` · `C` | escono dal layer, come se entrassi in insert mode, dopo aver spostato il cursore o aperto una riga dove serve |

Per tornare in "normal mode" riattiva il layer (doppio tocco di Shift destro). Tenendo premuto **Shift** o **Ctrl** con il layer attivo, l'overlay mostra cosa fanno i tasti con quel modificatore. Comandi composti (`dw`, `ci"`, conteggi come `3j`), registri e modalità visuale non sono riproducibili con un singolo tasto e non sono inclusi.

Le scorciatoie dipendono dall'applicazione: Ctrl+F e F3 funzionano nella maggior parte degli editor e dei browser, ma non dappertutto.

## Impostazioni

Fai clic sull'icona nella barra delle applicazioni (oppure riavvia l'exe mentre è già in esecuzione):

- **Profilo**: scegli il profilo da modificare e usare, caricane uno pronto, oppure creane uno nuovo, rinominalo, eliminalo o svuotalo;
- clicca un tasto sulla tastiera disegnata, oppure usa **«Premi un tasto…»**;
- scegli la variante: **da solo**, **con Shift** o **con Ctrl**. Per esempio `G` e `Shift+G` possono fare cose diverse. Con la variante selezionata, la tastiera mostra le mappature di quella variante;
- scegli dal menu a tendina «Diventa» in cosa trasformarlo (frecce, Home/Fine, PagSu/PagGiù, Canc, Esc, tasti multimediali, lettere, F1-F24, «Esci dal layer», …) e, se vuoi, aggiungi i modificatori Ctrl/Shift/Alt/Win (es. `Y` → `Ctrl+←` per saltare di una parola);
- **oppure** scrivi una **sequenza** di tasti separati da virgole, per esempio `End, Enter` o `Home, Shift+Down, Ctrl+X`. `LayerOff` spegne il layer in quel punto;
- facoltativo: un'**etichetta** breve da mostrare sul tasto nell'overlay (es. «undo»);
- premi **Assegna**. «Nessuno» disattiva il tasto mentre il layer è attivo;
- opzioni: tasto di attivazione, modalità, posizione, opacità e dimensione dell'overlay, avvio con Windows.

Le modifiche si salvano subito in `KeyLayer.ini`, accanto all'exe. Il file è di testo e si può modificare anche a mano (con l'app chiusa):

```ini
[Settings]
Profile=Personale
ToggleKey=RShift
ActivationMode=DoubleTap
DoubleTapMs=400
DisableStickyKeys=1
Opacity=90
Scale=100
Position=Auto

[Profile:Personale]
I=Up
J=Left
K=Down
L=Right
U=PageUp
O=PageDown
N=Home
M=End
Y=Ctrl+Left

[Profile:Vim]
G=Ctrl+Home | doc↑
Shift+G=Ctrl+End | doc↓
D=Home, Shift+Down, Ctrl+X | taglia
O=End, Enter, LayerOff | riga↓
```

Sintassi di una riga: `Tasto=Destinazione | etichetta`. Il tasto può avere il prefisso `Shift+` o `Ctrl+` (variante). La destinazione è un tasto con eventuali modificatori, oppure una sequenza separata da virgole. I file creati con le versioni precedenti, con la sezione `[Remap]`, vengono letti come profilo «Personale».

## Note

- **Portabilità:** tutto resta nella cartella dell'exe. L'unica eccezione è l'opzione "Avvia con Windows" (disattivata di default), che aggiunge una voce nel registro utente `HKCU\...\Run`; togliendo la spunta la voce viene rimossa. Se sposti la cartella, al primo avvio la voce viene aggiornata al nuovo percorso.
- **Finestre come amministratore:** per una regola di sicurezza di Windows (UIPI), un programma non elevato non può intercettare i tasti delle finestre aperte come amministratore. Per rimappare anche lì, avvia KeyLayer come amministratore.
- **Menu dell'icona:** Impostazioni, attiva/disattiva il layer, *Profilo* (cambio rapido), *Sospendi* (disattiva KeyLayer senza chiuderlo), apri cartella, esci.
- **Da riga di comando:** `KeyLayer.exe` con l'app già aperta apre le impostazioni; `KeyLayer.exe /exit` la chiude in modo pulito, ripristinando anche i Tasti permanenti; `/silent` la avvia senza notifica.

## Compilare

Lancia `build.bat`: usa il compilatore C# già presente in Windows (`%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe`) e genera `dist\KeyLayer.exe`.

```
src\RemapEngine.cs       hook tastiera/mouse (thread dedicato), modalità di attivazione, SendInput
src\SystemIntegration.cs desktop virtuali (IVirtualDesktopManager), Tasti permanenti / Filtro tasti
src\OverlayForm.cs       overlay trasparente, sempre in primo piano, che lascia passare i clic + tastiera cliccabile
src\KeyboardRenderer.cs  disegno della tastiera (condiviso tra overlay e impostazioni)
src\SettingsForm.cs      finestra delle impostazioni
src\TrayApp.cs           avvio, istanza singola, icona di notifica
src\AppConfig.cs         lettura/scrittura del file ini e avvio automatico
src\Layout.cs            layout ISO; le etichette seguono il layout di tastiera attivo
src\KeyNames.cs          nomi ed etichette dei tasti, mappature
```
