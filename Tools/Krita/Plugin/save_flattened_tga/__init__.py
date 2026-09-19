from krita import Krita
from .save_flattened_tga import SaveFlattenedTGAExtension

Krita.instance().addExtension(SaveFlattenedTGAExtension(Krita.instance()))
