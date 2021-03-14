#include <QApplication>
#include <set>
int main(int argc, char *argv[])
{
	QApplication a(argc, argv);
//	DLConfig::LoadFromFile(QApplication::applicationDirPath() + "/config.json");
	std::set<std::string> args;
	for (int i = 1; i < argc; ++i)
		args.insert(argv[i]);
	if (args.count("-u"))
		setvbuf(stdout, NULL, _IONBF, 0);
	return a.exec();
}
