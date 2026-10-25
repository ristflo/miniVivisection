import inspect
import weakref

_visualizers = {}
_inverses = {}

class NotInvertible(Exception):
    """Raised by an @inverse function when an edited geometry cannot be inverted into the original type."""
    pass

_VISUALIZE_MARKER = "__rhinoviv_visualize__"
_INVERSE_MARKER = "__rhinoviv_inverse__"


def _is_decorated_function(arg):
    """True when a decorator was applied bare (``@rhinoviv.visualize``), so it
    received the decorated function itself instead of a target type."""
    return callable(arg) and not isinstance(arg, type)


def _find_marked_method(cls, marker):
    """The first function in ``cls``'s MRO carrying ``marker``, or None. Plain
    functions in a class body are called as ``func(instance, ...)``, so the
    caller can use the result exactly like a registered free function."""
    for klass in getattr(cls, "__mro__", (cls,)):
        try:
            members = vars(klass).values()
        except TypeError:
            continue  # e.g. a .NET type without a Python __dict__
        for member in members:
            if getattr(member, marker, False):
                return member
    return None


def _type_names(cls):
    """The names a class can be registered under with ``@visualize("...")``:
    its plain name, its qualified name (``Outer.Inner``), and that qualified name
    prefixed with its module (``my_module.Outer.Inner``)."""
    name = getattr(cls, "__name__", "")
    qualname = getattr(cls, "__qualname__", name)
    module = getattr(cls, "__module__", "")
    return (name, qualname, f"{module}.{qualname}" if module else qualname)


def _lookup(registry, cls):
    """A registry entry for ``cls``: its own type, else a registered base type,
    else a class name along its MRO (most specific class first)."""
    if cls in registry:
        return registry[cls]
    for target, value in registry.items():
        if isinstance(target, type):
            try:
                if issubclass(cls, target):
                    return value
            except TypeError:
                continue
    for klass in getattr(cls, "__mro__", (cls,)):
        for name in _type_names(klass):
            if name in registry:
                return registry[name]
    return None


def visualize(target_type=None):
    """
    Registers a custom visualization, in any of these forms:

    - ``@rhinoviv.visualize(MyType)`` on a function ``f(obj)`` -- for types you
      can't (or don't want to) edit.
    - ``@rhinoviv.visualize("MyType")`` -- the same, by class name, where the
      class itself isn't in scope (e.g. in a visualizer script loaded while the
      debuggee is paused). Also matches subclasses' bases by name.
    - ``@rhinoviv.visualize`` on a method of the class itself, ``def _vis(self)``.

    The function returns standard Rhino geometry (Rhino.Geometry or rhino3dm), a
    struct like ``Point3d``, or plain data the bridge already understands (e.g. a
    list of ``(x, y, z)`` tuples).
    """
    if _is_decorated_function(target_type):
        setattr(target_type, _VISUALIZE_MARKER, True)
        return target_type

    def decorator(func):
        if target_type is None:  # ``@rhinoviv.visualize()`` -- same as the bare form
            setattr(func, _VISUALIZE_MARKER, True)
        else:
            _visualizers[target_type] = func
        return func
    return decorator

def get_visualizer(obj_or_type):
    """
    Returns the visualizer for the given object or type, or None if none matches:
    a function registered with ``@visualize(T)`` (exact type first, then base
    types, then by class name), else a method marked with bare ``@visualize`` on
    the class.
    """
    cls = obj_or_type if isinstance(obj_or_type, type) else type(obj_or_type)
    return _lookup(_visualizers, cls) or _find_marked_method(cls, _VISUALIZE_MARKER)

def inverse(target_type=None, mode="full"):
    """
    Registers the inverse of a visualizer -- used to write a geometry edited in
    Rhino back into the paused debuggee ("Pull from Rhino"). Either form:

    - ``@rhinoviv.inverse(MyType)`` on a function ``f(obj, geometry)``.
    - ``@rhinoviv.inverse("MyType")`` -- the same, by class name.
    - ``@rhinoviv.inverse`` on a method of the class, ``def _inv(self, geometry)``.

    Update ``obj`` in place and return None, or return a replacement value that
    is assigned to the watched expression. Raise ``NotInvertible`` to refuse.
    """
    if _is_decorated_function(target_type):
        setattr(target_type, _INVERSE_MARKER, True)
        return target_type

    def decorator(func):
        if target_type is None:  # ``@rhinoviv.inverse()`` -- same as the bare form
            setattr(func, _INVERSE_MARKER, True)
        else:
            _inverses[target_type] = (func, mode)
        return func
    return decorator

def get_inverse(target_type):
    """
    Returns (func, mode) for target_type, or None if none matches. Accepts either
    a type or an instance. Registered functions win over marked methods, the same
    precedence as get_visualizer.
    """
    t_type = target_type if isinstance(target_type, type) else type(target_type)
    registered = _lookup(_inverses, t_type)
    if registered is not None:
        return registered
    method = _find_marked_method(t_type, _INVERSE_MARKER)
    return (method, "full") if method is not None else None

# --- rhinoviv.watched ---------------------------------------------------------
#
# Values of the paused debuggee in the visualization Rhino. Reads come from the
# plugin's mirror (WatchMirror, filled at every pause while
# rhinoVivisection.mirrorValues is on). Changes -- and reads of what the mirror
# doesn't have -- go to the live value in the paused frame: WatchMirror.Request
# -> VS Code -> debug_visualizer_bridge.apply_watched_request. Each value knows
# where it lives, its ref: (root expression, path of ("attr", name) /
# ("item", key) segments). After a change VS Code re-reads every watched value
# before it answers, and every value still in use here re-reads the mirror.

_TAG = "__rhinoviv__"
_TIMEOUT = 10.0
_MISSING = object()
_proxies = {}  # id -> weakref, every value with a ref
_refresh_pass = [0]


def _watch_mirror():
    import clr
    clr.AddReference("RhinoVivisection.Core")
    import RhinoVivisection
    return RhinoVivisection.WatchMirror


def _child(ref, segment):
    return (ref[0], ref[1] + (segment,))


def _ref_text(ref):
    root, path = ref
    text = root
    for kind, key in path:
        if kind == "attr":
            text += "." + key
        elif isinstance(key, slice):
            bounds = ["" if b is None else repr(b) for b in (key.start, key.stop)]
            text += "[%s%s]" % (":".join(bounds), "" if key.step is None else ":%r" % (key.step,))
        else:
            text += "[%r]" % (key,)
    return text


def _request(ref, op, what, timeout=None, **fields):
    """Runs ``op`` on the debuggee's value at ``ref`` and returns VS Code's reply.
    Raises what the debuggee raised (the same type for built-in exceptions),
    RuntimeError when it couldn't be asked (not paused, not connected) and
    TimeoutError when VS Code doesn't answer."""
    import json
    import time

    text = json.dumps(dict(fields, op=op, root=ref[0], path=_encode_path(ref[1])), allow_nan=False)
    try:
        mirror = _watch_mirror()
        request_id = mirror.Request(text)
    except Exception as e:
        raise RuntimeError(f"rhinoviv.watched: {what}: {e}") from None
    limit = _TIMEOUT if timeout is None else timeout
    deadline = time.monotonic() + limit
    # The reply comes in on the plugin's network thread -- this one (Rhino's UI
    # thread) needn't be kept going for it.
    while True:
        reply = mirror.TakeReply(request_id)
        if reply is not None:
            break
        if time.monotonic() > deadline:
            raise TimeoutError(f"rhinoviv.watched: {what}: VS Code didn't answer within {limit:g} s (it may still happen)")
        time.sleep(0.01)
    reply = json.loads(reply)
    if not reply.get("ok"):
        _raise_remote(reply, what)
    if reply.get("changed"):
        _refresh_all()
    return reply


def _raise_remote(reply, what):
    import builtins

    message = reply.get("error") or "unknown error"
    error_type = reply.get("errorType")
    kind = getattr(builtins, error_type, None) if error_type else None
    error = None
    if isinstance(kind, type) and issubclass(kind, Exception):
        try:
            error = kind(f"{message} [in the debuggee: {what}]")
        except Exception:
            error = None
    if error is None:
        prefix = f"{error_type}: " if error_type else ""
        error = RuntimeError(f"rhinoviv.watched: {what}: {prefix}{message}")
    raise error from None


def _encode_path(path):
    return [[kind, key if kind == "attr" else _encode(key)] for kind, key in path]


def _encode(value):
    """``value`` for the debuggee: JSON, with tags for what JSON can't say
    (debug_visualizer_bridge._decode_value). A rhinoviv.watched value goes as a
    reference, so the debuggee assigns that same object."""
    import math

    if isinstance(value, _Proxy) and value.__dict__["_rv_ref"] is not None:
        ref = value.__dict__["_rv_ref"]
        return {_TAG: "ref", "root": ref[0], "path": _encode_path(ref[1])}
    if value is None or isinstance(value, (bool, int, str)):
        return value
    if isinstance(value, float):
        return value if math.isfinite(value) else {_TAG: "float", "v": repr(value)}
    if isinstance(value, list):
        return [_encode(v) for v in value]
    if isinstance(value, tuple):
        return {_TAG: "tuple", "v": [_encode(v) for v in value]}
    if isinstance(value, (set, frozenset)):
        return {_TAG: "frozenset" if isinstance(value, frozenset) else "set", "v": [_encode(v) for v in value]}
    if isinstance(value, dict):
        if _TAG not in value and all(isinstance(k, str) for k in value):
            return {k: _encode(v) for k, v in value.items()}
        return {_TAG: "dict", "v": [[_encode(k), _encode(v)] for k, v in value.items()]}
    if isinstance(value, slice):
        return {_TAG: "slice", "v": [_encode(value.start), _encode(value.stop), _encode(value.step)]}
    if isinstance(value, WatchedObject):
        raise TypeError(f"this {value.class_name} is a copy of a result, not a value in the debuggee, and can't be sent")
    geometry = _encode_geometry(value)
    if geometry is not None:
        return geometry
    raise TypeError(
        f"{type(value).__name__} can't be sent to the debuggee -- numbers, strings, booleans, None, lists, "
        "tuples, sets, dicts, Rhino geometry and rhinoviv.watched values can")


def _encode_geometry(value):
    """Rhino geometry of this Rhino, as debug_visualizer_bridge.decode_geometry
    reads it."""
    import json

    name = type(value).__name__
    xyz = lambda v: {"X": float(v.X), "Y": float(v.Y), "Z": float(v.Z)}  # noqa: E731
    if name in ("Point3d", "Point3f", "Vector3d", "Vector3f") and hasattr(value, "Z"):
        data = dict(xyz(value), type="Point3d" if name.startswith("Point") else "Vector3d")
    elif name == "Plane" and hasattr(value, "XAxis"):
        data = {"type": "Plane", "Origin": xyz(value.Origin), "XAxis": xyz(value.XAxis), "YAxis": xyz(value.YAxis)}
    elif callable(getattr(value, "ToJSON", None)):
        try:
            data = json.loads(str(value.ToJSON(None)))
        except Exception:
            return None
    else:
        return None
    return {_TAG: "geometry", "v": data, "type": name}


def _as_sendable(values):
    """An iterable argument as something _encode takes."""
    if isinstance(values, (str, list, tuple, set, frozenset, dict, _Proxy)):
        return values
    return list(values)


def _view(value, ref=None, owner=None, live=False):
    """The mirrored JSON (debug_visualizer_bridge._dump_structural's format) as
    Python values. With a ``ref`` -- where the value lives in the debuggee --
    lists, dicts and objects are WatchedList, WatchedDict, WatchedObject, and a
    value mirrored only as text is a WatchedValue. Without one (a result, not a
    value at a place in the debuggee) they're plain copies."""
    if isinstance(value, list):
        if ref is None:
            return [_view(v) for v in value]
        return WatchedList(value, ref, owner, live)
    if isinstance(value, dict):
        if "__class__" in value:
            return WatchedObject(value["__class__"], value.get("__attrs__", {}), ref, owner, live)
        if set(value) == {"__float__"}:
            return float(value["__float__"])
        if set(value) in ({"__repr__"}, {"__cycle__"}):
            text = value.get("__repr__", "<cycle>")
            return text if ref is None else WatchedValue(text, ref, owner, live)
        if ref is None:
            return {k: _view(v) for k, v in value.items()}
        return WatchedDict(value, ref, owner, live)
    return value


def _mirrored(ref, cache):
    """The mirrored JSON at ``ref``, or _MISSING."""
    import json

    root, path = ref
    if root not in cache:
        try:
            raw = _watch_mirror().GetValue(root)
            cache[root] = _MISSING if raw is None else json.loads(raw.GetRawText())
        except Exception:
            cache[root] = _MISSING
    value = cache[root]
    for kind, key in path:
        if value is _MISSING:
            break
        if kind == "attr":
            is_object = isinstance(value, dict) and "__class__" in value
            value = value.get("__attrs__", {}).get(key, _MISSING) if is_object else _MISSING
        elif isinstance(value, list) and type(key) is int and -len(value) <= key < len(value):
            value = value[key]
        elif isinstance(value, dict) and "__class__" not in value and str(key) in value:
            value = value[str(key)]
        else:
            value = _MISSING
    return value


def _refresh_all():
    """After a change in the debuggee -- VS Code has re-read every watched value
    by then -- every value still in use re-reads the mirror: top-level ones
    directly, parts through their owner. A part its owner replaced becomes
    top-level, so one the script still holds is re-read too."""
    _refresh_pass[0] += 1
    current = _refresh_pass[0]
    cache = {}
    while True:
        pending = []
        for reference in list(_proxies.values()):
            proxy = reference()
            if proxy is None:
                continue
            state = proxy.__dict__
            owner = state["_rv_owner"]
            if state["_rv_pass"] != current and (owner is None or owner() is None):
                pending.append(proxy)
        if not pending:
            return
        for proxy in pending:
            proxy.__dict__["_rv_pass"] = current
            try:
                proxy._rv_reload(cache)
            except Exception:
                pass


def _release(values):
    """Values a re-read replaces: no longer parts of their owner."""
    for value in values:
        if isinstance(value, _Proxy):
            value.__dict__["_rv_owner"] = None


class _Proxy:
    """What rhinoviv.watched values have in common: where the value lives in the
    debuggee (``_rv_ref``), the value it is part of (``_rv_owner``, which re-reads
    it), and the live value for anything the mirror doesn't have. Names starting
    with ``_rv_`` are its own."""

    def _rv_init(self, ref, owner, live):
        state = self.__dict__
        state["_rv_ref"] = ref
        state["_rv_owner"] = weakref.ref(owner) if owner is not None else None
        state["_rv_live"] = live  # read from the debuggee, not the mirror
        state["_rv_pass"] = 0
        if ref is not None:
            key = id(self)
            _proxies[key] = weakref.ref(self, lambda _, key=key: _proxies.pop(key, None))

    def _rv_reload(self, cache):
        state = self.__dict__
        dump = _mirrored(state["_rv_ref"], cache)
        if dump is _MISSING and state["_rv_live"]:
            try:
                reply = _request(state["_rv_ref"], "get", _ref_text(state["_rv_ref"]))
            except Exception:
                return
            dump = _MISSING if reply.get("callable") else reply.get("result")
        if dump is not _MISSING:
            self._rv_update(dump)

    def _rv_target(self, segment=None):
        ref = self.__dict__["_rv_ref"]
        if ref is None:
            raise TypeError("this value is a copy of a result, not a value in the debuggee -- changing it changes nothing there")
        return ref if segment is None else _child(ref, segment)

    def _rv_get(self, segment):
        """The live value at ``segment``, read from the paused debuggee -- for a
        method, something that calls it there."""
        target = self._rv_target(segment)
        reply = _request(target, "get", _ref_text(target))
        if reply.get("callable"):
            return _LiveCallable(target)
        return _view(reply.get("result"), target, live=True)

    def _rv_set(self, segment, value):
        target = self._rv_target(segment)
        if isinstance(value, _Proxy) and value.__dict__["_rv_ref"] == target:
            return  # `x[k] += y`: the in-place operator has written it back already
        _request(target, "set", _ref_text(target) + " = ...", value=_encode(value))

    def _rv_delete(self, segment):
        target = self._rv_target(segment)
        _request(target, "del", "del " + _ref_text(target))

    def _rv_call(self, method, *args, **kwargs):
        return _LiveCallable(self._rv_target(("attr", method)))(*args, **kwargs)

    def _rv_func(self, name, *args, self_at=0):
        target = self._rv_target()
        reply = _request(target, "func", "%s(%s)" % (name, _ref_text(target)),
                         name=name, args=[_encode(a) for a in args], selfAt=self_at)
        return _view(reply.get("result"))

    def _rv_inplace(self, name, other):
        self._rv_func(name, other)
        return self

    def __getattr__(self, name):
        # Only for names neither mirrored nor the type's own: a property, a
        # method or a value the mirror doesn't have, read from the live value.
        # hasattr() is False when it can't be read (e.g. not paused).
        if name.startswith("__") or name.startswith("_rv_") or self.__dict__.get("_rv_ref") is None:
            raise AttributeError(name)
        try:
            return self._rv_get(("attr", name))
        except AttributeError:
            raise
        except Exception as e:
            raise AttributeError(f"{name}: {e}") from None

    def __setattr__(self, name, value):
        if name.startswith("_rv_"):
            self.__dict__[name] = value
        else:
            self._rv_set(("attr", name), value)

    def __delattr__(self, name):
        self._rv_delete(("attr", name))


class _LiveCallable:
    """A method (or other callable) of a debuggee value: calling it calls it in
    the paused debuggee; the result comes back as a copy."""

    def __init__(self, ref):
        self._rv_ref = ref

    def __call__(self, *args, **kwargs):
        ref = self._rv_ref
        reply = _request(ref, "call", _ref_text(ref) + "(...)",
                         args=[_encode(a) for a in args], kwargs={k: _encode(v) for k, v in kwargs.items()})
        return _view(reply.get("result"))

    def __repr__(self):
        return "<debuggee callable %s>" % _ref_text(self._rv_ref)


class _Operators:
    """Operators of the live value, in the debuggee: ``a + b`` returns a copy of
    the result, ``a += b`` changes the debuggee's ``a`` as Python would."""


def _binary(name, self_at):
    return lambda self, other: self._rv_func(name, other, self_at=self_at)


def _inplace(name):
    return lambda self, other: self._rv_inplace(name, other)


def _unary(name):
    return lambda self: self._rv_func(name)


for _name in ("add", "sub", "mul", "matmul", "truediv", "floordiv", "mod", "pow", "lshift", "rshift", "and", "or", "xor"):
    _function = _name + "_" if _name in ("and", "or") else _name
    setattr(_Operators, "__%s__" % _name, _binary(_function, 0))
    setattr(_Operators, "__r%s__" % _name, _binary(_function, 1))
    setattr(_Operators, "__i%s__" % _name, _inplace("i" + _name))
for _name in ("neg", "pos", "abs", "invert"):
    setattr(_Operators, "__%s__" % _name, _unary(_name))


class WatchedList(_Proxy, list):
    """A list of the paused debuggee -- or a tuple, set, ...: the mirror has
    those as lists. Reads like a list; assigning or deleting items, the list
    methods and ``+=``/``*=`` change the live value, by name (so a tuple
    refuses, as it would there, and a set's ``add()`` works)."""

    def __init__(self, dump, ref, owner=None, live=False):
        list.__init__(self)
        self._rv_init(ref, owner, live)
        self._rv_update(dump)

    def _rv_update(self, dump):
        if not isinstance(dump, list):
            return
        ref = self.__dict__["_rv_ref"]
        items = [_view(v, _child(ref, ("item", i)), self) for i, v in enumerate(dump)]
        _release(list(list.__iter__(self)))
        list.__setitem__(self, slice(None), items)

    def __setitem__(self, key, value):
        self._rv_set(("item", key), value)

    def __delitem__(self, key):
        self._rv_delete(("item", key))

    def append(self, value):
        self._rv_call("append", value)

    def extend(self, values):
        self._rv_call("extend", _as_sendable(values))

    def insert(self, index, value):
        self._rv_call("insert", index, value)

    def pop(self, index=-1):
        return self._rv_call("pop", index)

    def remove(self, value):
        self._rv_call("remove", value)

    def clear(self):
        self._rv_call("clear")

    def reverse(self):
        self._rv_call("reverse")

    def sort(self, *, key=None, reverse=False):
        if key is None:
            self._rv_call("sort", reverse=reverse)
        else:
            # The key function can't run in the debuggee: sorted here, and the
            # live list reordered with its own items.
            self[:] = sorted(self, key=key, reverse=reverse)

    def __iadd__(self, other):
        return self._rv_inplace("iadd", _as_sendable(other))

    def __imul__(self, n):
        return self._rv_inplace("imul", n)

    def __reduce_ex__(self, protocol):  # copy, deepcopy, pickle: a plain list
        return (list, (list(self),))


class WatchedDict(_Proxy, dict):
    """A dict of the paused debuggee. Reads like a dict -- a non-str key finds
    its entry, though the mirror has str(key); assigning or deleting items and
    the dict methods change the live value."""

    def __init__(self, dump, ref, owner=None, live=False):
        dict.__init__(self)
        self._rv_init(ref, owner, live)
        self._rv_update(dump)

    def _rv_update(self, dump):
        if not isinstance(dump, dict) or "__class__" in dump:
            return
        ref = self.__dict__["_rv_ref"]
        items = {k: _view(v, _child(ref, ("item", k)), self) for k, v in dump.items()}
        _release(list(dict.values(self)))
        dict.clear(self)
        dict.update(self, items)

    def __getitem__(self, key):
        try:
            return dict.__getitem__(self, key)
        except KeyError:
            if not isinstance(key, str) and dict.__contains__(self, str(key)):
                return dict.__getitem__(self, str(key))
            raise

    def __contains__(self, key):
        return dict.__contains__(self, key) or (not isinstance(key, str) and dict.__contains__(self, str(key)))

    def get(self, key, default=None):
        try:
            return self[key]
        except KeyError:
            return default

    def __setitem__(self, key, value):
        self._rv_set(("item", key), value)

    def __delitem__(self, key):
        self._rv_delete(("item", key))

    def update(self, *args, **kwargs):
        self._rv_call("update", *(_as_sendable(a) for a in args), **kwargs)

    def pop(self, key, *default):
        return self._rv_call("pop", key, *default)

    def popitem(self):
        return tuple(self._rv_call("popitem"))

    def setdefault(self, key, default=None):
        self._rv_call("setdefault", key, default)
        return self[key]

    def clear(self):
        self._rv_call("clear")

    def __ior__(self, other):
        return self._rv_inplace("ior", other)

    def __reduce_ex__(self, protocol):  # copy, deepcopy, pickle: a plain dict
        return (dict, (dict(self),))


class WatchedObject(_Operators, _Proxy):
    """An object of the paused debuggee: its mirrored attributes under their
    names (``node.x``), ``class_name`` its type. Assigning or deleting an
    attribute changes the live object; anything else -- properties, methods,
    items, operators -- is used on the live object. A copy of a result (a
    method's return value) is read-only."""

    def __init__(self, class_name, attrs, ref=None, owner=None, live=False):
        self._rv_init(ref, owner, live)
        self.__dict__["class_name"] = class_name
        self.__dict__["_rv_attrs"] = {}
        self._rv_update({"__class__": class_name, "__attrs__": attrs})

    def _rv_update(self, dump):
        if not isinstance(dump, dict) or "__class__" not in dump:
            return
        state = self.__dict__
        ref = state["_rv_ref"]
        old = state["_rv_attrs"]
        new = {k: _view(v, None if ref is None else _child(ref, ("attr", k)), self)
               for k, v in dump.get("__attrs__", {}).items()}
        _release(old.values())
        for name in old:
            if name not in new:
                state.pop(name, None)
        state.update(new)
        state["_rv_attrs"] = new
        state["class_name"] = dump["__class__"]

    def __setattr__(self, name, value):
        if not name.startswith("_rv_") and self.__dict__["_rv_ref"] is None:
            raise AttributeError(f"this {self.class_name} is a copy of a result, not a value in the debuggee -- read-only")
        _Proxy.__setattr__(self, name, value)

    def __delattr__(self, name):
        if self.__dict__["_rv_ref"] is None:
            raise AttributeError(f"this {self.class_name} is a copy of a result, not a value in the debuggee -- read-only")
        _Proxy.__delattr__(self, name)

    def __getitem__(self, key):
        return self._rv_get(("item", key))

    def __setitem__(self, key, value):
        self._rv_set(("item", key), value)

    def __delitem__(self, key):
        self._rv_delete(("item", key))

    def __call__(self, *args, **kwargs):
        return _LiveCallable(self._rv_target())(*args, **kwargs)

    def __repr__(self):
        inner = ", ".join("%s=%r" % item for item in self.__dict__["_rv_attrs"].items())
        return "%s(%s)" % (self.class_name, inner)

    def __reduce_ex__(self, protocol):  # copy, deepcopy, pickle: a read-only copy
        return (_object_copy, (self.class_name, dict(self.__dict__["_rv_attrs"])))


def _object_copy(class_name, attrs):
    copy = WatchedObject(class_name, {})
    copy.__dict__.update(attrs)
    copy.__dict__["_rv_attrs"] = dict(attrs)
    return copy


class WatchedValue(_Operators, _Proxy):
    """A debuggee value the mirror has only as text (its repr) -- a .NET object,
    a numpy array, ...: prints as that text and compares equal to it; attributes,
    items, calls, len(), iteration, ``in`` and operators use the live value."""

    def __init__(self, text, ref, owner=None, live=False):
        self._rv_init(ref, owner, live)
        self.__dict__["_rv_text"] = text

    def _rv_update(self, dump):
        if isinstance(dump, dict) and set(dump) in ({"__repr__"}, {"__cycle__"}):
            self.__dict__["_rv_text"] = dump.get("__repr__", "<cycle>")

    def __repr__(self):
        return self.__dict__["_rv_text"]

    __str__ = __repr__

    def __format__(self, spec):
        return format(str(self), spec)

    def __eq__(self, other):
        return str(self) == other if isinstance(other, str) else NotImplemented

    def __hash__(self):
        return hash(str(self))

    def __bool__(self):
        return True

    def __len__(self):
        return self._rv_func("len")

    def __iter__(self):
        target = self._rv_target()
        reply = _request(target, "func", "list(%s)" % _ref_text(target), name="list", args=[])
        return iter([_view(v, _child(target, ("item", i)), live=True) for i, v in enumerate(reply.get("result") or [])])

    def __contains__(self, item):
        return bool(self._rv_func("contains", item))

    def __getitem__(self, key):
        return self._rv_get(("item", key))

    def __setitem__(self, key, value):
        self._rv_set(("item", key), value)

    def __delitem__(self, key):
        self._rv_delete(("item", key))

    def __call__(self, *args, **kwargs):
        return _LiveCallable(self._rv_target())(*args, **kwargs)

    def __reduce_ex__(self, protocol):  # copy, deepcopy, pickle: its text
        return (str, (str(self),))


class _WatchedReader:
    """``rhinoviv.watched``: the values on VS Code's "Watching" list, in the
    visualization Rhino (``rhinoVivisection.mirrorValues`` on), as of the last
    pause. Changing one changes the paused debuggee -- ``watched.node.x = 10``,
    ``watched.coordinates[0][0] = 1``, ``watched.data["metadata"].append("c")``,
    ``watched.speed += 1`` -- and every value in use is re-read afterwards."""

    @property
    def available(self):
        try:
            import clr
            clr.AddReference("RhinoVivisection.Core")
            return True
        except Exception:
            return False

    def names(self):
        try:
            return list(_watch_mirror().GetNames())
        except Exception:
            return []

    def get(self, name):
        """The watched value ``name`` as of the last pause, or None when it isn't mirrored."""
        import json

        try:
            raw = _watch_mirror().GetValue(name)
        except Exception as e:
            raise AttributeError(f"rhinoviv.watched failed to retrieve '{name}': {e}") from None
        if raw is None:
            return None
        return _view(json.loads(raw.GetRawText()), (name, ()))

    def __getattr__(self, name):
        if name.startswith("_"):
            raise AttributeError(name)
        val = self.get(name)
        if val is None:
            raise AttributeError(
                f"'{name}' is not currently mirrored in rhinoviv.watched -- it must be on VS Code's Watching list, "
                "with rhinoVivisection.mirrorValues on, and the debugger must have paused since")
        return val

    def __getitem__(self, name):
        return self.get(name)

    def set(self, target, value, timeout=None):
        """``target = value`` in the paused debuggee, in the frame VS Code shows --
        any assignable target there, watched or not: ``"node.x"``,
        ``"data['speed']"``, a plain name. Returns when VS Code has re-read every
        watched value.

        ``value``: numbers, strings, booleans, None, lists, tuples, sets, dicts,
        Rhino geometry, or a rhinoviv.watched value -- then the debuggee assigns
        that same object. Raises what the debuggee raised (NameError,
        SyntaxError for a target that can't be assigned, ...), RuntimeError when
        VS Code couldn't be asked (not paused, not connected) and TimeoutError
        when it doesn't answer within ``timeout`` seconds (10)."""
        ref = (target.strip(), ())
        if isinstance(value, _Proxy) and value.__dict__["_rv_ref"] == ref:
            return  # `watched.x += y`: the in-place operator has written it back already
        _request(ref, "set", ref[0] + " = ...", timeout=timeout, value=_encode(value))

    def __setattr__(self, name, value):
        self.set(name, value)

    def __setitem__(self, name, value):
        self.set(name, value)

    def __delattr__(self, name):
        _request((name, ()), "del", "del " + name)

    def __delitem__(self, name):
        _request((name.strip(), ()), "del", "del " + name)


watched = _WatchedReader()
