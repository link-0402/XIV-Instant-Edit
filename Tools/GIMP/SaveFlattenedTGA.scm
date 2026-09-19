; Save Flattened TGA (In Place)
;
; Exports the active image as a flattened 32-bit Targa (RGBA, 8 bits/channel),
; overwriting the file it was opened/exported from. Runs on a throwaway
; duplicate of the image, so your open image's layers are left untouched
; and stay editable afterward.
;
; Targets GIMP 2.10.x Script-Fu. GIMP 3.0 changed a lot of the underlying
; PDB/introspection API; if you're on 3.0 and this errors, see the note
; about PRECISION-U8-NON-LINEAR below.
;
; Install: copy this file into GIMP's scripts folder, then
; Filters > Script-Fu > Refresh Scripts (or restart GIMP). It then appears
; as File > Export > Save Flattened TGA (In Place), and can be bound to a
; keyboard shortcut via Edit > Keyboard Shortcuts.
;
; NOTE ON PRECISION-U8-NON-LINEAR: I couldn't verify this constant's exact
; name against a live GIMP install. If this script errors on the
; gimp-image-convert-precision line, open Filters > Script-Fu > Console and
; type:
;     (gimp-image-convert-precision (car (gimp-image-list)) 150)
; then check the Procedure Browser (Help > Procedure Browser, search
; "convert-precision") for the exact constant your version expects, and
; swap it in below.

(define (script-fu-save-flattened-tga image drawable)
  (let* ((filename (car (gimp-image-get-filename image)))
         (fname-len (string-length filename)))
    (cond
      ((= fname-len 0)
       (gimp-message "Save Flattened TGA: this image has never been saved/exported to a file."))

      ((or (< fname-len 4)
           (not (string=? (string-downcase (substring filename (- fname-len 4) fname-len)) ".tga")))
       (gimp-message (string-append "Save Flattened TGA: \"" filename "\" is not a .tga file.")))

      (else
        (let* ((dup (car (gimp-image-duplicate image))))
          (gimp-image-convert-precision dup PRECISION-U8-NON-LINEAR)
          (gimp-image-merge-visible-layers dup CLIP-TO-IMAGE)
          (let* ((flat (car (gimp-image-get-active-drawable dup))))
            (if (= (car (gimp-drawable-has-alpha flat)) FALSE)
                (gimp-layer-add-alpha flat))
            (file-tga-save RUN-NONINTERACTIVE dup flat filename filename))
          (gimp-image-delete dup))))
    (gimp-displays-flush)))

(script-fu-register
  "script-fu-save-flattened-tga"
  "Save Flattened TGA (In Place)"
  "Export a flattened 32-bit TGA over the file this image was opened from, without altering the open image's layers."
  "Instant Edit Tools"
  "Instant Edit Tools"
  "2026"
  "*"
  SF-IMAGE    "Image"    0
  SF-DRAWABLE "Drawable" 0)

(script-fu-menu-register "script-fu-save-flattened-tga" "<Image>/File/Export")
