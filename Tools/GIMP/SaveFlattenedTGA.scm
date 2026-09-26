; Save Flattened TGA (In Place) / Save Flattened TGA As Variant...
;
; Save Flattened TGA (In Place) exports the active image as a flattened 32-bit
; Targa (RGBA, 8 bits/channel), overwriting the .tga the image was opened or
; exported from.
;
; Save Flattened TGA As Variant... asks for a name and writes the same kind of
; TGA as <name>.tga next to that file instead; the image stays linked to its
; own file. Instant Edit adds every extra TGA saved in a texture's working
; folder as a variant: a Penumbra option named after the file. Saving under
; the same name again updates that variant.
;
; Both work on a throwaway duplicate of the image, so your open image's layers
; are left untouched and stay editable afterward.
;
; Works in GIMP 2.10 and GIMP 3. Where the two differ, the script checks which
; procedure exists before calling it.
;
; Install: copy this file into GIMP's scripts folder and restart GIMP. Both
; commands then appear in the File menu next to Export As..., and can be bound
; to keyboard shortcuts via Edit > Keyboard Shortcuts.

; The file the image was opened from, saved to or exported to; "" if none.
(define (instant-edit-image-file image)
  ; GIMP 3 renamed gimp-image-get-filename to gimp-image-get-file.
  (car (if (defined? 'gimp-image-get-file)
           (gimp-image-get-file image)
           (gimp-image-get-filename image))))

; The image's top-level layers. GIMP 2.10 returns (count #(ids)), GIMP 3 (#(ids)).
(define (instant-edit-image-layers image)
  (let ((result (gimp-image-get-layers image)))
    (if (vector? (car result)) (car result) (cadr result))))

; Writes the image's visible layers, merged, to FILENAME as an uncompressed
; 32-bit TGA.
(define (instant-edit-export-flattened-tga image filename)
  (let* ((dup (car (gimp-image-duplicate image)))
         ; Merging, not flattening: flatten would discard the alpha channel.
         ; The merged layer always has alpha, and a lone visible layer keeps
         ; the colour under its fully transparent pixels.
         (merged (car (gimp-image-merge-visible-layers dup CLIP-TO-IMAGE)))
         (layers (instant-edit-image-layers dup)))
    ; Hidden layers survive the merge. Drop them, so the export sees only the
    ; merged layer and GIMP 3 doesn't merge it again.
    (let loop ((i 0))
      (if (< i (vector-length layers))
          (begin
            (if (not (= (vector-ref layers i) merged))
                (gimp-image-remove-layer dup (vector-ref layers i)))
            (loop (+ i 1)))))
    (if (= (car (gimp-drawable-is-rgb merged)) FALSE)
        (gimp-image-convert-rgb dup))
    (if (= (car (gimp-drawable-has-alpha merged)) FALSE)
        (gimp-layer-add-alpha merged))
    ; The merge only covers the visible layers' extent.
    (gimp-layer-resize-to-image-size merged)
    ; Uncompressed, bottom-left origin. The exporter writes 8 bits/channel
    ; whatever the image's precision.
    (if (defined? 'file-tga-export)
        (file-tga-export RUN-NONINTERACTIVE dup filename -1 FALSE "bottom-left")
        (file-tga-save RUN-NONINTERACTIVE dup merged filename filename FALSE 1))
    (gimp-image-delete dup)))

(define (instant-edit-tga-file? path)
  (let ((len (string-length path)))
    (and (>= len 4)
         (string=? (string-downcase (substring path (- len 4) len)) ".tga"))))

; PATH up to and including its last separator; "" when it has none.
(define (instant-edit-folder-of path)
  (let loop ((i (- (string-length path) 1)))
    (cond ((< i 0) "")
          ((memv (string-ref path i) (string->list "/\\")) (substring path 0 (+ i 1)))
          (else (loop (- i 1))))))

; Drops surrounding spaces and a typed ".tga", which the save adds anyway.
(define (instant-edit-clean-variant-name name)
  (let ((trimmed (string-trim name)))
    (if (instant-edit-tga-file? trimmed)
        (string-trim (substring trimmed 0 (- (string-length trimmed) 4)))
        trimmed)))

(define (instant-edit-invalid-file-name? name)
  (or (string=? name ".")
      (string=? name "..")
      (let loop ((i 0))
        (and (< i (string-length name))
             (let ((c (string-ref name i)))
               (or (< (char->integer c) 32)
                   (memv c (string->list "\\/:*?\"<>|"))
                   (loop (+ i 1))))))))

; Why Instant Edit can't use NAME for a variant, or #f when it can.
(define (instant-edit-variant-name-problem name)
  (let ((lower (string-downcase name)))
    (cond
      ((= (string-length name) 0)
       "Enter a name for the variant.")
      ((> (string-length name) 120)
       "Keep the name to 120 characters or fewer.")
      ((instant-edit-invalid-file-name? name)
       (string-append "\"" name "\" isn't a valid file name. Leave out \\ / : * ? \" < > |"))
      ((string=? lower "original")
       "\"Original\" is reserved for the edited texture. Pick another name.")
      ((or (string=? lower "texture") (string=? lower "snapshot"))
       (string-append "Instant Edit uses " lower ".tga itself. Pick another name."))
      (else #f))))

(define (script-fu-save-flattened-tga image drawables)
  (let ((filename (instant-edit-image-file image)))
    (cond
      ((= (string-length filename) 0)
       (gimp-message "Save Flattened TGA: this image has never been saved or exported to a file."))
      ((not (instant-edit-tga-file? filename))
       (gimp-message (string-append "Save Flattened TGA: \"" filename "\" is not a .tga file.")))
      (else
       (instant-edit-export-flattened-tga image filename)))))

(define (script-fu-save-flattened-tga-variant image drawables name)
  (let* ((filename (instant-edit-image-file image))
         (variant (instant-edit-clean-variant-name name))
         (problem (instant-edit-variant-name-problem variant)))
    (cond
      ((= (string-length filename) 0)
       (gimp-message "Save Flattened TGA As Variant: this image has never been saved or exported to a file."))
      (problem
       (gimp-message (string-append "Save Flattened TGA As Variant: " problem)))
      (else
       (instant-edit-export-flattened-tga
         image (string-append (instant-edit-folder-of filename) variant ".tga"))))))

; GIMP 3 deprecates script-fu-register and enables commands registered with it
; only while exactly one layer is selected, so it gets script-fu-register-filter.
; The run functions receive the selected drawable (2.10) or a vector of them (3)
; as their second argument and don't use it.
(if (defined? 'script-fu-register-filter)
    (begin
      (script-fu-register-filter
        "script-fu-save-flattened-tga"
        "Save Flattened TGA (In Place)"
        "Export a flattened 32-bit TGA over the file this image was opened from, without altering the open image's layers."
        "Instant Edit Tools"
        "Instant Edit Tools"
        "2026"
        "*"
        SF-ONE-OR-MORE-DRAWABLE)
      (script-fu-register-filter
        "script-fu-save-flattened-tga-variant"
        "Save Flattened TGA As Variant..."
        "Export a flattened 32-bit TGA named after the variant next to the file this image was opened from, without altering the open image's layers."
        "Instant Edit Tools"
        "Instant Edit Tools"
        "2026"
        "*"
        SF-ONE-OR-MORE-DRAWABLE
        SF-STRING "Variant name" ""))
    (begin
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
      (script-fu-register
        "script-fu-save-flattened-tga-variant"
        "Save Flattened TGA As Variant..."
        "Export a flattened 32-bit TGA named after the variant next to the file this image was opened from, without altering the open image's layers."
        "Instant Edit Tools"
        "Instant Edit Tools"
        "2026"
        "*"
        SF-IMAGE    "Image"    0
        SF-DRAWABLE "Drawable" 0
        SF-STRING   "Variant name" "")))

(script-fu-menu-register "script-fu-save-flattened-tga" "<Image>/File/Export")
(script-fu-menu-register "script-fu-save-flattened-tga-variant" "<Image>/File/Export")
