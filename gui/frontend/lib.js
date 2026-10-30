// Pure helpers for editing LiteDB extended-JSON documents. No DOM.
// Editor model shape: { type, value }
//   null, minValue, maxValue : value is null
//   string                   : value is a string
//   bool                     : value is a boolean
//   int, long, double, decimal, date, oid, guid, binary : value is a string
//   array                    : value is an array of models
//   object                   : value is an array of { key, model }

(function (global) {
  "use strict";

  var TYPES = [
    "null", "string", "int", "long", "double", "decimal",
    "bool", "date", "oid", "guid", "binary",
    "minValue", "maxValue", "array", "object"
  ];

  var WRAPPERS = {
    $oid: "oid",
    $date: "date",
    $numberLong: "long",
    $numberDecimal: "decimal",
    $binary: "binary",
    $guid: "guid",
    $minValue: "minValue",
    $maxValue: "maxValue"
  };

  var GUID_RE = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
  var OID_RE = /^[0-9a-f]{24}$/i;
  var LONG_RE = /^-?\d+$/;
  var DECIMAL_RE = /^-?\d+(\.\d+)?$/;
  var DOUBLE_RE = /^-?\d+(\.\d+)?([eE][+-]?\d+)?$/;
  var BASE64_RE = /^[A-Za-z0-9+/]*={0,2}$/;

  function detectType(value) {
    if (value === null) return "null";
    var t = typeof value;
    if (t === "boolean") return "bool";
    if (t === "string") return "string";
    if (t === "number") return Number.isInteger(value) ? "int" : "double";
    if (Array.isArray(value)) return "array";
    if (t === "object") {
      var keys = Object.keys(value);
      if (keys.length === 1) {
        var mapped = WRAPPERS[keys[0]];
        if (mapped !== undefined) return mapped;
      }
      return "object";
    }
    return "string";
  }

  function toModel(value) {
    var type = detectType(value);
    switch (type) {
      case "null":
      case "minValue":
      case "maxValue":
        return { type: type, value: null };
      case "string":
        return { type: type, value: value };
      case "bool":
        return { type: type, value: value };
      case "int":
      case "double":
        return { type: type, value: String(value) };
      case "long":
        return { type: type, value: value.$numberLong };
      case "decimal":
        return { type: type, value: value.$numberDecimal };
      case "date": {
        // normalize epoch-ms number to ISO so the editor can display it
        if (typeof value.$date === "number") {
          const d = new Date(value.$date);
          return { type: type, value: isNaN(d.getTime()) ? String(value.$date) : d.toISOString() };
        }
        return { type: type, value: value.$date };
      }
      case "oid":
        return { type: type, value: value.$oid };
      case "guid":
        return { type: type, value: value.$guid };
      case "binary":
        return { type: type, value: typeof value.$binary === "string" ? value.$binary : "" };
      case "array":
        return { type: type, value: value.map(toModel) };
      case "object": {
        var fields = Object.keys(value).map(function (key) {
          return { key: key, model: toModel(value[key]) };
        });
        return { type: type, value: fields };
      }
    }
    throw new Error("cannot model value: " + JSON.stringify(value));
  }

  function defaultModel(type) {
    switch (type) {
      case "null":
      case "minValue":
      case "maxValue":
        return { type: type, value: null };
      case "string":
        return { type: type, value: "" };
      case "int":
      case "long":
        return { type: type, value: "0" };
      case "double":
      case "decimal":
        return { type: type, value: "0" };
      case "bool":
        return { type: type, value: false };
      case "date":
        return { type: type, value: new Date().toISOString() };
      case "oid":
        return { type: type, value: "" };
      case "guid":
        return { type: type, value: "" };
      case "binary":
        return { type: type, value: "" };
      case "array":
        return { type: type, value: [] };
      case "object":
        return { type: type, value: [] };
    }
    throw new Error("unknown type: " + type);
  }

  function validate(type, raw) {
    var v = raw == null ? "" : String(raw);
    switch (type) {
      case "int":
        if (!/^-?\d+$/.test(v) || Math.abs(Number(v)) > 9007199254740991) throw new Error("int: not a whole number");
        return Number(v);
      case "long":
        if (!LONG_RE.test(v)) throw new Error("long: must be an integer");
        return v;
      case "double":
        if (!DOUBLE_RE.test(v)) throw new Error("double: invalid number");
        return Number(v);
      case "decimal":
        if (!DECIMAL_RE.test(v)) throw new Error("decimal: invalid number");
        return v;
      case "date": {
        // LiteDB extended JSON may carry epoch ms (number) or ISO string
        var d = typeof v === "number" ? new Date(v) : new Date(String(v));
        if (isNaN(d.getTime())) throw new Error("date: invalid date");
        return d.toISOString();
      }
      case "oid":
        if (!OID_RE.test(v)) throw new Error("oid: must be 24 hex characters");
        return v.toLowerCase();
      case "guid":
        if (!GUID_RE.test(v)) throw new Error("guid: invalid guid");
        return v;
      case "binary":
        if (v.length % 4 !== 0 || !BASE64_RE.test(v)) throw new Error("binary: must be base64");
        return v;
      case "string":
        return v;
      case "bool":
        return raw === true || raw === "true";
    }
    throw new Error("unknown type: " + type);
  }

  // Build extended JSON (plain JS value) from the editor model.
  function fromModel(model) {
    var type = model.type;
    var v = model.value;
    switch (type) {
      case "null":
        return null;
      case "minValue":
        return { $minValue: "1" };
      case "maxValue":
        return { $maxValue: "1" };
      case "bool":
        return v === true;
      case "int":
        return validate("int", v);
      case "long":
        return { $numberLong: validate("long", v) };
      case "double":
        return validate("double", v);
      case "decimal":
        return { $numberDecimal: validate("decimal", v) };
      case "date":
        return { $date: validate("date", v) };
      case "oid":
        return { $oid: validate("oid", v) };
      case "guid":
        return { $guid: validate("guid", v) };
      case "binary":
        return { $binary: validate("binary", v) };
      case "string":
        return String(v == null ? "" : v);
      case "array":
        return v.map(fromModel);
      case "object": {
        var out = {};
        for (var i = 0; i < v.length; i++) {
          var field = v[i];
          if (!field.key) throw new Error("field name is required");
          if (out.hasOwnProperty(field.key)) throw new Error("duplicate field: " + field.key);
          out[field.key] = fromModel(field.model);
        }
        return out;
      }
    }
    throw new Error("unknown type: " + type);
  }

  // ISO (UTC) -> datetime-local input value (fields show UTC)
  function dateToInput(iso) {
    var d = new Date(iso == null ? "" : iso);
    if (isNaN(d.getTime())) return "";
    function p(n) { return String(n).padStart(2, "0"); }
    return d.getUTCFullYear() + "-" + p(d.getUTCMonth() + 1) + "-" + p(d.getUTCDate()) +
      "T" + p(d.getUTCHours()) + ":" + p(d.getUTCMinutes()) + ":" + p(d.getUTCSeconds());
  }

  // datetime-local input value (UTC fields) -> ISO (UTC)
  function inputToDate(local) {
    var s = String(local).trim();
    if (s && !/[zZ]|[+-]\d{2}:\d{2}$/.test(s)) s += "Z";
    var d = new Date(s);
    if (isNaN(d.getTime())) throw new Error("date: invalid date");
    return d.toISOString();
  }

  var api = {
    TYPES: TYPES,
    detectType: detectType,
    toModel: toModel,
    defaultModel: defaultModel,
    fromModel: fromModel,
    validate: validate,
    dateToInput: dateToInput,
    inputToDate: inputToDate
  };

  if (typeof module !== "undefined" && module.exports) module.exports = api;
  global.LiteDbGui = api;
})(typeof window !== "undefined" ? window : globalThis);
