// pch.h: Dies ist eine vorkompilierte Headerdatei.
// Die unten aufgeführten Dateien werden nur einmal kompiliert, um die Buildleistung für zukünftige Builds zu verbessern.
// Dies wirkt sich auch auf die IntelliSense-Leistung aus, Codevervollständigung und viele Features zum Durchsuchen von Code eingeschlossen.
// Die hier aufgeführten Dateien werden jedoch ALLE neu kompiliert, wenn mindestens eine davon zwischen den Builds aktualisiert wird.
// Fügen Sie hier keine Dateien hinzu, die häufig aktualisiert werden sollen, da sich so der Leistungsvorteil ins Gegenteil verkehrt.

#ifndef PCH_H
#define PCH_H

// Fügen Sie hier Header hinzu, die vorkompiliert werden sollen.
#include "framework.h"

// ============================================================
// OpenCV-Header — externe Bibliothek, Code-Analyse deaktiviert
// Die folgenden Warnungen stammen ausschließlich aus OpenCV-
// internen Headern und betreffen NICHT den eigenen Code.
//
// C26495 type.6  : Nicht initialisierte Membervariable
// C26440 f.6     : Funktion sollte noexcept deklariert sein
// C26446         : Index liegt außerhalb des gültigen Bereichs
// C26426         : Statischer Initialisierer (keine constexpr)
// C26812         : Enum ohne Bereichsangabe
// C26818         : Switch-Anweisung deckt nicht alle Fälle ab
// C6385 / C6386  : Mögliche Pufferüberläufe (SAL-Analyse)
// ============================================================
#pragma warning(push)
#pragma warning(disable: \
    26495 \
    26440 \
    26446 \
    26426 \
    26427 \
    26812 \
    26818 \
    26819 \
    26434 \
    26451 \
    26450 \
    26453 \
    26457 \
    26481 \
    26482 \
    26485 \
    26486 \
    26487 \
    26489 \
    26494 \
    26496 \
    6385  \
    6386  \
    6011  \
    4244  \
    4267  \
    4305  \
    4996  )

#include <opencv2/opencv.hpp>
#include <opencv2/objdetect.hpp>

#pragma warning(pop)
// ============================================================

#endif //PCH_H
