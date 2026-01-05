#include <iostream>
#include <fstream>
#include <sstream>
#include "MiniJspp.hpp"

static std::string readFile(const char* path) {
    std::ifstream f(path, std::ios::binary);
    std::ostringstream ss;
    ss << f.rdbuf();
    return ss.str();
}

int main(int argc, char** argv) {

    if (argc < 2) {
        std::cout << "usage: " << argv[0] << " <script.js>\n";
        return 1;
    }

    minijspp::Engine js;

    // 1) globale Funktion hostAdd(a,b)
    js.registerFunction("hostAdd", [](const std::vector<minijspp::Value>& args, const minijspp::Value&) {
        double a = args.size() > 0 ? args[0].toNumber() : 0.0;
        double b = args.size() > 1 ? args[1].toNumber() : 0.0;
        return minijspp::Value::Number(a + b);
        });

    // 2) Klasse Counter: constructor(v){ this.x=v }  inc(){ this.x++; return this.x }
    auto counter = js.createClass("Counter");

    counter.addMethod("constructor", js.createFunction([&js](const std::vector<minijspp::Value>& args, const minijspp::Value& thisVal) {
        // thisVal ist ein Objekt
        minijspp::Object self(minijspp::Value::Handle(minijspp::Value::Kind::Object, thisVal.handle(), /*retain*/true));
        double v = args.size() > 0 ? args[0].toNumber() : 0.0;
        self.set(js, "x", minijspp::Value::Number(v));
        return minijspp::Value::Null();
        }));

    counter.addMethod("inc", js.createFunction([&js](const std::vector<minijspp::Value>&, const minijspp::Value& thisVal) {
        minijspp::Object self(minijspp::Value::Handle(minijspp::Value::Kind::Object, thisVal.handle(), /*retain*/true));
        double x = self.get("x").toNumber();
        x += 1.0;
        self.set(js, "x", minijspp::Value::Number(x));
        return minijspp::Value::Number(x);
        }));

    // ins Global-Scope stellen (ownership geht an Runtime)
    js.declareMove("Counter", counter.toValueMove());

    std::string code = readFile(argv[1]);
    std::string ret = js.run(code);

    std::cout << "minijs_run returned: " << ret << "\n";
    return 0;
}
