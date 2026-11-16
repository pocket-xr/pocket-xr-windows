"""English only for now. The Deck's tr() and N_() are kept so its modules move over unchanged."""


def tr(text, **kw):
    return text.format(**kw) if kw else text


def N_(text):
    return text
