/* SPDX-License-Identifier: MIT
 * Import-only variants of the no-CRT bootstrap probe. None of these referenced
 * APIs is invoked. Their addresses only keep the corresponding PE imports live.
 */
#include "BootstrapProbe.c"

#ifdef PROBE_COMCTL
#include <commctrl.h>
void* volatile probe_common_control_imports[] = {
    (void*)&SetWindowSubclass, (void*)&RemoveWindowSubclass, (void*)&DefSubclassProc
};
#endif

#ifdef PROBE_SHELL
#include <ole2.h>
#include <shlwapi.h>
void* volatile probe_shell_imports[] = {
    (void*)&CoCreateInstance, (void*)&CoTaskMemFree,
    (void*)&RegisterDragDrop, (void*)&RevokeDragDrop, (void*)&StrRetToStrW
};
#endif
